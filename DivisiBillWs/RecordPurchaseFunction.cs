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

    internal static async Task<bool> RecordAsync(AndroidPurchase? androidPurchase, bool isSubscription, ILogger logger, LicenseStore licenseStore)
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
            if (isSubscription)
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
            if (!verifiedWithStore)
                logger.LogError("In RecordPurchaseFunction, could not verify purchase with Google");
            else
            {
                logger.LogInformation("In RecordPurchaseFunction, successfully verified {AcknowledgementState} purchase with Google, checking license table", verifiedAcknowledgementState == 1 ? "acknowledged" : "unacknowledged");
                // All is well so far and we have a legitimately issued license
                // Now ensure it is not already known and if not, remember it for the future
                bool recorded = await licenseStore.RecordAsync(androidPurchase);
                if (recorded && verifiedAcknowledgementState == 0)
                {
                    if (isSubscription)
                        LicenseCheck.AcknowledgeSubscriptionPurchase(androidPurchase.PackageName, androidPurchase.ProductId, androidPurchase.PurchaseToken);
                    else
                        LicenseCheck.AcknowledgeProductPurchase(androidPurchase.PackageName, androidPurchase.ProductId, androidPurchase.PurchaseToken);
                }
                return recorded;
            }
        }
        return false;
    }

    /// Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
    /// a the function code may be simultaneously executed on multiple threads.
    [Function("recordpurchase")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest httpRequest)
    {
        logger.LogInformation("The 'recordpurchase' web service function is processing a request.");
        // Get the options passed in the query string
        var query = httpRequest.Query;
        string subscription = query["subscription"].ToString();
        AndroidPurchase? androidPurchase;
        try
        {
            androidPurchase = await AndroidPurchase.FromJsonAsync(httpRequest.Body);
            logger.LogInformation($"successfully deserialized androidPurchase from request body");
            if (string.IsNullOrWhiteSpace(androidPurchase?.OrderId))
                return new BadRequestResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "In 'recordpurchase', Exception deserializing product");
            return new BadRequestResult();
        }

        bool recorded = await RecordAsync(androidPurchase, isSubscription: subscription.Equals("1"), logger, licenseStore);

        return recorded
            ? new OkResult()
            : new BadRequestResult();
    }
}
