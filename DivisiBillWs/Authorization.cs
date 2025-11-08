using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace DivisiBillWs;
internal class Authorization
{
    internal const string PurchaseHeaderName = "divisibill-android-purchase";
    internal const string TokenHeaderName = "divisibill-token";
    internal Authorization(ILogger loggerParam, LicenseStore licenseStoreParam)
    {
        logger = loggerParam;
        licenseStore = licenseStoreParam;
    }
    private readonly LicenseStore licenseStore;
    private readonly ILogger logger;

    /// <summary>
    /// <para>Authorizes an HTTP request based on a header and returns the user key if the user is authorized.This is 
    /// called only for functions requiring authorization.</para>
    /// <para> Authorization comes in 3 forms:</para>
    /// <list type="number">
    /// <item>A header containing a purchase response from some license server (currently only the Android play store).</item>
    /// <item>A token indicating that a purchase response was validated earlier.</item>
    /// <item>An OCR license embedded in a multi-part HTTP request (only for the Scan function).</item>
    /// </list>
    /// This function is only used for pro subscriptions or licenses (the first two cases).
    /// <para>The terms 'license' and 'purchase' are often synonymous, it's called a license in most of the code, but we avoid
    /// using the word license as much as possible in user facing text. Generally we handle either subscriptions or one time 
    /// licenses, collectively called 'purchases' and commonly encoded as an <see cref="AndroidPurchase"/> object.</para> 
    /// </summary>
    /// <param name="httpRequest">The incoming request</param>
    /// <returns>The userKey for this license if the request passed authorization checks, null otherwise</returns>
    public async Task<string?> GetAuthorizedUserKeyAsync(HttpRequest httpRequest)
    {
        // Early exit if there is a current token that matches (meaning we do not need to check the license in androidPurchase)
        var tokenHeader = httpRequest.Headers.FirstOrDefault((x) => x.Key.ToLowerInvariant().Equals(TokenHeaderName));
        if (tokenHeader.Key != null)
        {
            string? incomingToken = tokenHeader.Value.FirstOrDefault();
            string? userKey = licenseStore.GetUserKeyFromToken(incomingToken);
            if (userKey != null)
                return userKey;// early exit, a valid token was provided, so no need to recheck the purchase license
        }
        // If we reach here then authorize the hard way, by looking at the license
        // for old license records with no obfuscated account id we return the order id instead
        AndroidPurchase? androidPurchase = await ProLicenseFromRequestAsync(logger, httpRequest); // Extract a pro purchase from the header
        return (androidPurchase != null && await GetIsAuthorizedAsync(androidPurchase))
            ? androidPurchase.ObfuscatedAccountId ?? androidPurchase.OrderId! // This is a new pro licensee legitimately issued 
            : null;
    }

    /// <summary>
    /// Evaluate whether a purchase passed as authorization with a request to do something is known by us and by the 
    /// app store, called indirectly via <see cref="GetAuthorizedUserKeyAsync"/> by middleware for functions requiring
    /// authorization of an <see cref="AndroidPurchase"/>  object representing a pro purchase before they'll do work (which is most functions).
    /// <see cref="ScanFunction"/> calls it directly for an OCR license, mostly for historical reasons.
    ///
    /// There's an explicit purchase validation call <see cref="GetIsVerifiedAsync"/> which is used to authenticate a purchase and
    /// store it away if it's a pro purchase so it can be used in other calls where it will be validated by this function.
    /// 
    /// The terms 'license' and 'purchase' are often synonymous, it's called a license in most of the code, but we avoid
    /// using the word license as much as possible in user facing text where we often need to distinguish a one-time purchase 
    /// of OCR scans from the expiring Pro subscription.
    /// </summary>
    /// <param name="androidPurchase">The incoming object to be verified</param>
    /// <returns>True if the request passed authorization checks</returns>
    public async Task<bool> GetIsAuthorizedAsync(AndroidPurchase androidPurchase)
    {
        if (androidPurchase.OrderId == null || androidPurchase.ProductId == null || androidPurchase.PurchaseToken == null)
            return false;
        // First see if we've heard of it, then see if the store has (because checking our tables is cheaper)
        int i = await licenseStore.GetScansAsync(androidPurchase);
        if (i >= 0 // we heard of it, even if it has no scans left
              && PlayStore.VerifyPurchase(logger, androidPurchase)) // See if the play store is happy with it
            return true;
        else
            logger.LogError("In GetIsAuthorizedAsync, error licenseStore.GetScans returned {Scans}", i);
        return false;
    }

    /// <summary>
    /// Extract the pro purchase (if any) passed in a request (in its own header called <see cref="PurchaseHeaderName"/>)
    /// and verify that its signature matches the stored signature and that it is a pro purchase.
    /// </summary>
    /// <param name="loggerParam">Standard logging instance</param>
    /// <param name="httpRequest">Incoming HttpRequest - used as a sort of read-only state variable</param>
    /// <returns></returns>
    internal async Task<AndroidPurchase?> ProLicenseFromRequestAsync(ILogger loggerParam, HttpRequest httpRequest)
    {
        AndroidPurchase? androidPurchase = null;

        // First, we need to get the purchase record from the license header
        var purchaseHeader = httpRequest.Headers.FirstOrDefault((x) => x.Key.ToLowerInvariant().Equals(PurchaseHeaderName));
        string androidPurchaseJson = string.Empty;
        if (purchaseHeader.Key != null)
        {
            string? x = purchaseHeader.Value.FirstOrDefault();
            if (x != null)
            {
                androidPurchaseJson = Uri.UnescapeDataString(x);
                try
                {
                    androidPurchase = AndroidPurchase.FromJson(androidPurchaseJson);
                }
                catch (Exception ex)
                {
                    loggerParam.LogError(ex, "In ProLicenseFromRequest, exception deserializing product from header: {Message}", ex.Message);
                }
            }
        }
        if (androidPurchase != null
            && !string.IsNullOrWhiteSpace(androidPurchase.OrderId)
            && (androidPurchase.GetIsLicenseFor(LicenseStore.ProSubscriptionId)
               || androidPurchase.GetIsLicenseFor(LicenseStore.ProSubscriptionIdOld))) // Support license testers by not requiring them to keep renewing subscriptions
        {     
            // This looks like a pro purchase so verify it against the stored signature to ensure it was issued by the Play Store
            if (!await licenseStore.VerifyAgainstStoredSignatureAsync(androidPurchase.OrderId, androidPurchaseJson))
                return null;
            // We still have not verified it against the Play Store to ensure the purchase is still valid
            return androidPurchase;
        }
        else
            return null; // Not a valid pro license
    }

    // TODO: Delete this function once all clients are updated to store signatures
    /// <summary>
    /// <para>Called by the verify function to validate a license issued by an app store. The license is passed as the request body.
    /// Assuming it passes verification then if the license is a pro purchase it is going to be passed in a header to be used 
    /// for future authentication <see cref="GetIsAuthorizedAsync"/> so it is stored there. Other license types (like OCR licenses) are just validated.</para> 
    /// 
    /// <para>The last use time of the license is updated whenever this function is called by calling <see cref="LicenseStore.UpdateTimeUsedAsync"/>.
    /// This update is done for administrative convenience, so old "stale" licenses are more easily detected./></para> 
    /// 
    /// <para>This is called at least once to verify every license DivisiBill uses.</para>
    /// </summary>
    /// <param name="httpRequest">The incoming HttpRequest object</param>
    /// 
    /// <returns>The number of remaining scans allocated to this license</returns>
    internal async Task<IActionResult> GetIsVerifiedAsync(HttpRequest httpRequest)
    {
        AndroidPurchase? androidPurchase;

        try
        {
            androidPurchase = await httpRequest.ReadFromJsonAsync<AndroidPurchase>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "In GetIsVerified, exception deserializing product: {Message}", ex.Message);
            androidPurchase = null;
        }

        // If we reach here then check the license against the play store and our store
        if (androidPurchase != null && PlayStore.VerifyPurchase(logger, androidPurchase))
        {
            // These are to allay the compiler's concerns...
            Debug.Assert(androidPurchase.OrderId != null);
            Debug.Assert(androidPurchase.ProductId != null);

            int scans = await licenseStore.GetScansAsync(androidPurchase!); // Tells us it is one of ours

            if (scans >= 0)
            {
                await licenseStore.UpdateTimeUsedAsync(androidPurchase.OrderId);
                return new OkObjectResult(scans.ToString());
            }
        }
        return new BadRequestResult();
    }
    /// <summary>
    /// <para>Called by the <see cref="VerifyFunction.VerifyAndroidPurchase"/> function to validate a purchase issued by the android app store.
    /// The purchase JSON is passed as one field in the request body and the purchase signature is passed as the other.
    /// Assuming it passes verification then if the purchase is a pro purchase it is going to be passed in a header to be used 
    /// for future authentication <see cref="GetIsAuthorizedAsync"/>. Other purchase types (like OCR licenses) are just validated.</para> 
    /// 
    /// <para>The last use time of the purchase is updated whenever this function is called by calling <see cref="LicenseStore.UpdateTimeUsedAsync"/>.
    /// This update is done for administrative convenience, so old "stale" licenses are more easily detected./></para> 
    /// 
    /// <para>This is called at least once per run to verify every license DivisiBill uses in that run.</para>
    /// </summary>
    /// <param name="httpRequest">The incoming HttpRequest object</param>
    /// <returns>The number of remaining scans allocated to this license</returns>
    internal async Task<IActionResult> VerifyAndroidPurchase(HttpRequest httpRequest)
    {
        // Ensure content is a form
        if (!httpRequest.ContentType?.Contains("application/x-www-form-urlencoded") ?? true)
            return new BadRequestObjectResult("Invalid content type");

        // Read form data
        var formData = await httpRequest.ReadFormAsync();

        string? purchaseJson = formData["purchase"];
        string? signature = formData["signature"];

        if (string.IsNullOrEmpty(purchaseJson))
            return new BadRequestObjectResult("license content not found");

        if (string.IsNullOrEmpty(signature))
            return new BadRequestObjectResult("signature content not found");

        // Now verify that the license was signed by the play store
        if (!PlayStore.VerifyDivisiBillPurchaseSignature(purchaseJson, signature))
            return new BadRequestObjectResult("signature verification failed");

        // Now deserialize the license
        AndroidPurchase? androidPurchase;
        try
        {
            androidPurchase = AndroidPurchase.FromJson(purchaseJson);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "In GetIsVerified, exception deserializing product: {Message}", ex.Message);
            androidPurchase = null;
        }

        // If we reach here then check the license against the play store and our store (we know it was issued by the
        // Play Store because the signature was verified but we do not know if it is still current or is one of ours).
        if (androidPurchase != null )
        {
            // These are to allay the compiler's concerns...
            Debug.Assert(androidPurchase.OrderId != null);
            Debug.Assert(androidPurchase.ProductId != null);
            // Verify against Play Store to ensure the purchase is still valid
            if (!PlayStore.VerifyPurchase(logger, androidPurchase))
                return new BadRequestObjectResult("unable to verify purchase");
            // Now see if we know about this purchase and signature
            int scans = await licenseStore.GetScansAsync(androidPurchase!);
            if (scans >= 0)
            {
                // At this point, we know it's one we've heard of so if it is acknowledged we can use the signature
                if (androidPurchase.Acknowledged)
                {
                    // Verify that the signature matches the stored signature or store it if we don't have it yet
                    if (!await licenseStore.VerifyOrStoreSignatureAsync(androidPurchase.OrderId, signature))
                        return new BadRequestObjectResult("signature does not match stored signature");
                }
                await licenseStore.UpdateTimeUsedAsync(androidPurchase.OrderId);
                return new OkObjectResult(scans.ToString());
            }
            else
                return new BadRequestObjectResult("the purchase is valid but not known to us");
        }
        return new BadRequestObjectResult("Unknown failure in GetIsVerifiedAsyncV2");
    }
}