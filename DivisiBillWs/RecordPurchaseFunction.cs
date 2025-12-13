using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DivisiBillWs;

public class RecordPurchaseFunction
{
    private readonly ILogger logger;

    public RecordPurchaseFunction(ILoggerFactory loggerFactory)
    {
        logger = loggerFactory.CreateLogger<RecordPurchaseFunction>();
        licenseStore = new LicenseStore(logger);
    }

    private readonly LicenseStore licenseStore;

    /// <summary>
    /// Attempts to record an Android purchase in the license store after verifying its validity with the Google Play
    /// Store.
    /// </summary>
    /// <remarks>Purchases are expected to be validated by checking their signature before calling this. Only purchases 
    /// for the expected package name are processed. The method verifies the purchase is current 
    /// (using the Google Play Store) before recording it. If the purchase is not acknowledged, the method attempts to
    /// acknowledge it after recording. All validation and verification failures are logged using the provided
    /// logger. 
    /// <para>It might seem logical to record the purchase signature here but that is not a good idea because the purchase probably 
    /// has not been acknowledged yet and when it is, its signature will change.</para></remarks>
    /// <param name="androidPurchase">The Android purchase to be recorded. Must not be null and must contain valid package name, product ID, order ID,
    /// obfuscated account ID, and purchase token.</param>
    /// <param name="logger">The logger used to record informational and error messages during the operation. Cannot be null.</param>
    /// <param name="licenseStore">The license store in which to record the purchase. Cannot be null.</param>
    /// <returns>true if the purchase was successfully verified and recorded in the license store; otherwise, false.</returns>
    internal static async Task<bool> RecordAsync(AndroidPurchase? androidPurchase, ILogger logger, LicenseStore licenseStore)
    {
        if (androidPurchase == null)
            logger.LogError("In RecordPurchaseFunction, could not deserialize a purchase with an OrderId");
        else if (string.IsNullOrEmpty(androidPurchase.PackageName))
            logger.LogError("In RecordPurchaseFunction, could not extract a {PropertyName}", nameof(AndroidPurchase.PackageName));
        else if (!androidPurchase.PackageName.Equals(LicenseStore.ExpectedPackageName)) // only DivisiBill Licenses can be used
            logger.LogError("In RecordPurchaseFunction, package name was not com.autoplus.divisibill: {PackageName}", androidPurchase.PackageName);
        else if (string.IsNullOrEmpty(androidPurchase.ProductId))
            logger.LogError("In RecordPurchaseFunction, could not extract a {PropertyName}", nameof(AndroidPurchase.ProductId));
        else if (string.IsNullOrEmpty(androidPurchase.OrderId))
            logger.LogError("In RecordPurchaseFunction, could not extract a {PropertyName}", nameof(AndroidPurchase.OrderId));
        else if (string.IsNullOrEmpty(androidPurchase.ObfuscatedAccountId))
            logger.LogError("In RecordPurchaseFunction, could not extract an {PropertyName}", nameof(AndroidPurchase.ObfuscatedAccountId));
        else if (string.IsNullOrEmpty(androidPurchase.PurchaseToken))
            logger.LogError("In RecordPurchaseFunction, could not extract a {PropertyName}", nameof(AndroidPurchase.PurchaseToken));
        else
        {
            logger.LogInformation("In RecordPurchaseFunction, PackageName:{PackageName}, OrderId:{OrderId}, ProductId:{ProductId}, Quantity:{Quantity}, ObfuscatedAccountid:{ObfuscatedAccountId}, PurchaseToken:{PurchaseToken}",
                androidPurchase.PackageName, androidPurchase.OrderId, androidPurchase.ProductId, androidPurchase.Quantity, androidPurchase.ObfuscatedAccountId, androidPurchase.PurchaseToken);
            bool verifiedWithStore = false;
            int? verifiedAcknowledgementState = null;
            if (androidPurchase.IsSubscription)
            {
                Google.Apis.AndroidPublisher.v3.Data.SubscriptionPurchaseV2? verifiedPurchase;
                try
                {
                    verifiedPurchase = LicenseCheck.GetSubscriptionPurchase(
                        androidPurchase.PackageName, androidPurchase.PurchaseToken);
                    if (verifiedPurchase != null)
                    {
                        verifiedWithStore = true;
                        verifiedAcknowledgementState = verifiedPurchase.AcknowledgementState.Equals("ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED") ? 1 : 0;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("In RecordPurchaseFunction, exception calling Google to check purchase: {ExceptionMessage}", ex.Message);
                }
            }
            else
            {
                try
                {
                    Google.Apis.AndroidPublisher.v3.Data.ProductPurchase? verifiedPurchase = LicenseCheck.GetProductPurchase(
                        androidPurchase.PackageName, androidPurchase.ProductId, androidPurchase.PurchaseToken);
                    if (verifiedPurchase != null)
                    {
                        verifiedWithStore = true;
                        verifiedAcknowledgementState = verifiedPurchase.AcknowledgementState;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("In RecordPurchaseFunction, exception calling Google to check purchase: {ExceptionMessage}", ex.Message);
                }
            }
            if (verifiedWithStore)
            {
                logger.LogInformation("In RecordPurchaseFunction, successfully verified {AcknowledgementState} purchase with Google, checking license table", verifiedAcknowledgementState == 1 ? "acknowledged" : "unacknowledged");
                // All is well so far and we have a legitimately issued license
                // Now ensure it is not already known and if not, remember it for the future
                bool recorded = await licenseStore.RecordAsync(androidPurchase);
                if (recorded && verifiedAcknowledgementState == 0)
                {
                    if (androidPurchase.IsSubscription)
                        LicenseCheck.AcknowledgeSubscriptionPurchase(androidPurchase.PackageName, androidPurchase.ProductId, androidPurchase.PurchaseToken);
                    else
                        LicenseCheck.AcknowledgeProductPurchase(androidPurchase.PackageName, androidPurchase.ProductId, androidPurchase.PurchaseToken);
                }
                return recorded;
            }
            else
                logger.LogError("In RecordPurchaseFunction, could not verify purchase with Google");
        }
        return false;
    }

    /// Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
    /// a the function code may be simultaneously executed on multiple threads.
    [Function(nameof(RecordAndroidPurchase))]
    public async Task<IActionResult> RecordAndroidPurchase([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest httpRequest)
    {
        logger.LogInformation("The '{name}' web service function is processing a request.", nameof(RecordAndroidPurchase));
        // Ensure content is a form
        if (!httpRequest.ContentType?.Contains("application/x-www-form-urlencoded") ?? true)
            return new BadRequestObjectResult("Invalid content type");
        AndroidPurchase? androidPurchase;
        try
        {
            // Read form data
            var formData = await httpRequest.ReadFormAsync();

            string? purchaseJson = formData["purchase"];
            string? signature = formData["signature"];

            if (string.IsNullOrEmpty(purchaseJson))
                return new BadRequestObjectResult("license content not found");

            if (string.IsNullOrEmpty(signature))
                return new BadRequestObjectResult("signature content not found");

            // Verify that the license was signed by the play store
            if (!PlayStore.VerifyDivisiBillPurchaseSignature(purchaseJson, signature))
                return new BadRequestObjectResult("signature verification failed");

            // Deserialize the (now trusted) license
            androidPurchase = AndroidPurchase.FromJson(purchaseJson);

            logger.LogInformation($"successfully deserialized androidPurchase from request");
            if (string.IsNullOrWhiteSpace(androidPurchase?.OrderId))
                return new BadRequestResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "In 'recordpurchase', Exception deserializing product");
            return new BadRequestResult();
        }

        bool recorded = await RecordAsync(androidPurchase, logger, licenseStore);

        return recorded
            ? new OkResult()
            : new BadRequestResult();
    }
}
