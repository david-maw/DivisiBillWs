using System.Diagnostics;

namespace DivisiBillWs
{
    internal class Authorization
    {
        internal const string PurchaseHeaderName = "divisibill-android-purchase";
        internal const string TokenHeaderName = "divisibill-token";
        internal Authorization(ILogger loggerParam, LicenseStore licenseStoreParam)
        {
            logger = loggerParam;
            licenseStore = licenseStoreParam;
        }

        LicenseStore licenseStore;

        ILogger logger;

        /// <summary>
        /// Gets an authorized user key, called from functions requiring authorization. Authorization comes in 3 forms:
        /// 1) A header containing a license response from some license server (initially the Android play store)
        /// 2) A token indicating that a license response was validated earlier
        /// 3) A license embedded in a multi-part HTTP request
        /// This always comes after a call to GetIsVerified (below) for the Pro license and the only license used
        /// for verification is a Pro one (in fact it's used when verifying other license types - just OCR today).
        /// 
        /// The terms 'license' and 'product' are often synonymous, it's called a license in most of the code, but we avoid
        /// using the word license as much as possible in user facing text. 
        /// </summary>
        /// <param name="req">The incoming request</param>
        /// <returns>The userKey for this license if the request passed authorization checks, null otherwise</returns>
        public async Task<String?> GetAuthorizedUserKeyAsync(HttpRequestData req)
        {
            // Early exit if there is a current token that matches (meaning we do not need to check the androidPurchase)
            var tokenHeader = req.Headers.FirstOrDefault((x) => x.Key.ToLowerInvariant().Equals(TokenHeaderName));
            if (tokenHeader.Key != null)
            {
                string? incomingToken = tokenHeader.Value.FirstOrDefault();
                string? userKey = licenseStore.GetUserKeyFromToken(incomingToken);
                if (userKey != null)
                    return userKey;// early exit, a valid token was provided, so no need to recheck the purchase license
            }
            // If we reach here then authorize the hard way, by looking at the license  
            AndroidPurchase? androidPurchase = ProLicenseFromRequest(logger, req); // Extract a pro license from the header
            if (androidPurchase != null && await GetIsAuthorizedAsync(androidPurchase))
            {
                return androidPurchase.ObfuscatedAccountId ?? androidPurchase.OrderId!; // This is a new pro licensee legitimately issued 
            }
            return null;
        }
        /// <summary>
        /// Evaluate whether an alleged license is authorized, called indirectly via <see cref="GetAuthorizedUserKeyAsync"/> from 
        /// functions requiring authorization of an AndroidPurchase object before they'll do work (which is most functions).
        /// <see cref="ScanFunction"/> calls it directly, mostly for historical reasons.
        /// 
        /// The terms 'license' and 'product' are often synonymous, it's called a license in most of the code, but we avoid
        /// using the word license as much as possible in user facing text. 
        /// </summary>
        /// <param name="androidPurchase">The incoming object to be verified</param>
        /// <returns>True if the request passed authorization checks</returns>
        public async Task<bool> GetIsAuthorizedAsync(AndroidPurchase androidPurchase) 
        {
            if (androidPurchase.OrderId == null || androidPurchase.ProductId==null) 
                return false;
            // First see if we've heard of it, then see if the store has (because checking our tables is cheaper)
            int i = await licenseStore.GetScansAsync(androidPurchase.OrderId);
            if (i >= 0 // we heard of it, even if it has no scans left
                  && PlayStore.VerifyPurchase(logger, androidPurchase, isSubscription: androidPurchase.ProductId.EndsWith(".subscription"))) // See if the play store is happy with it
                return true;
            else
                logger.LogError("In GetUserKey, error licenseStore.GetScans returned " + i);
            return false;
        }

        /// <summary>
        /// Extract the pro license (if any) passed in a request
        /// </summary>
        /// <param name="loggerParam">Standard logging instance</param>
        /// <param name="req">Incoming HttpRequest - used as a sort of read-only state variable</param>
        /// <returns></returns>
        internal static AndroidPurchase? ProLicenseFromRequest(ILogger loggerParam, HttpRequestData req)
        {
            AndroidPurchase? androidPurchase = null;

            // First, we need to get the purchase record
            var purchaseHeader = req.Headers.FirstOrDefault((x) => x.Key.ToLowerInvariant().Equals(PurchaseHeaderName));

            if (purchaseHeader.Key != null)
            {
                string? x = purchaseHeader.Value.FirstOrDefault();
                if (x != null)
                {
                    string androidPurchaseJson = Uri.UnescapeDataString(x);
                    try
                    {
                        androidPurchase = AndroidPurchase.FromJson(androidPurchaseJson);
                    }
                    catch (Exception ex)
                    {
                        loggerParam.LogError(ex, "In ProLicenseFromRequest, exception deserializing product from header");
                    }
                }
            }
            return androidPurchase != null
                && (androidPurchase.GetIsLicenseFor(LicenseStore.ProSubscriptionId)
                   || androidPurchase.GetIsLicenseFor(LicenseStore.ProSubscriptionIdOld)) // Temporary down level support
                ? androidPurchase : null;
        }
        /// <summary>
        /// Called by the verify function to validate a license issued by an app store. The license is passed as the request body.
        /// Assuming it passes verification then if the license is a pro license it is going to be passed in a header to be used 
        /// for future authentication, if it any other kind of license (an OCR license today) a pro license must be passed in a 
        /// header. If it is an OCR license the count of scans it has will be added to the pro count and its own count will be 
        /// zeroed. This is called to verify every license DivisiBill uses.
        /// </summary>
        /// <param name="req">The incoming HttpRequest object</param>
        /// <returns>The OrderId of the license</returns>
        /// <param name="isSubscription">Whether the license being verified is for a subscription or a product</param>
        internal async Task<HttpResponseData> GetIsVerifiedAsync(HttpRequestData req, bool isSubscription)
        {
            AndroidPurchase? androidPurchase = null;

            try
            {
                androidPurchase = await req.ReadFromJsonAsync<AndroidPurchase>();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "In GetIsVerified, exception deserializing product");
                androidPurchase = null;
            }

            // If we reach here then check the license against the play store and our store
            if (androidPurchase != null && PlayStore.VerifyPurchase(logger, androidPurchase, isSubscription))
            {
                // These are to allay the compiler's concerns...
                Debug.Assert(androidPurchase.OrderId != null);
                Debug.Assert(androidPurchase.ProductId != null);

                int scans = await licenseStore.GetScansAsync(androidPurchase.OrderId!); // Tells us it is one of ours

                if (scans == -1)
                {
                    // The license was verified by the Play Store but not known to us; that's no great problem, just store it and try again
                    bool recorded = await RecordPurchaseFunction.RecordAsync(androidPurchase, isSubscription, logger, licenseStore);
                    if (recorded)
                        scans = await licenseStore.GetScansAsync(androidPurchase.OrderId!);
                }

                if (scans >= 0)
                {
                    // if and only if we were verifying a pro license then generate a token
                    if (androidPurchase.ProductId.Equals(LicenseStore.ProSubscriptionId) || androidPurchase.ProductId.Equals(LicenseStore.ProSubscriptionIdOld))
                    {
                        string userkey = androidPurchase.ObfuscatedAccountId ?? androidPurchase.OrderId; // It's a legitimate pro OrderId, verified as one of ours
                        return await req.OkResponseAsync(licenseStore.GetTokenIfNew(userkey), scans.ToString());
                    }
                    return await req.OkResponseAsync(null, scans.ToString()); 
                }
            }
            return await req.MakeResponseAsync(HttpStatusCode.BadRequest);
        }
    }
}
