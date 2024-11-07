using System.Text.Json;

namespace DivisiBillWs
{
    public class RecordPurchaseFunction
    {
        private readonly ILogger logger;

        public RecordPurchaseFunction(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger<RecordPurchaseFunction>();
            licenseStore = new LicenseStore(logger);
        }

        LicenseStore licenseStore;

        public async Task<bool> RecordAsync(AndroidPurchase? androidPurchase, bool isSubscription)
        {
            if (androidPurchase == null)
                logger.LogError("In RecordPurchaseFunction, could not deserialize a purchase with an OrderId");
            else if (string.IsNullOrEmpty(androidPurchase.PackageName))
                logger.LogError("In RecordPurchaseFunction, could not extract a " + nameof(AndroidPurchase.PackageName));
            else if (!androidPurchase.PackageName.Equals(LicenseStore.ExpectedPackageName)) // only DivisiBill Licenses can be used
                logger.LogError("In RecordPurchaseFunction, package name was not com.autoplus.divisibill: " + androidPurchase.PackageName);
            else if (string.IsNullOrEmpty(androidPurchase.ProductId))
                logger.LogError("In RecordPurchaseFunction, could not extract a " + nameof(AndroidPurchase.ProductId));
            else if (string.IsNullOrEmpty(androidPurchase.OrderId))
                logger.LogError("In RecordPurchaseFunction, could not extract a " + nameof(AndroidPurchase.OrderId));
            else if (string.IsNullOrEmpty(androidPurchase.ObfuscatedAccountId))
                logger.LogError("In RecordPurchaseFunction, could not extract an " + nameof(AndroidPurchase.ObfuscatedAccountId));
            else if (string.IsNullOrEmpty(androidPurchase.PurchaseToken))
                logger.LogError("In RecordPurchaseFunction, could not extract a " + nameof(AndroidPurchase.PurchaseToken));
            else
            {
                logger.LogInformation($"""
                    In RecordPurchaseFunction 
                        PackageName:{androidPurchase.PackageName}
                        OrderId:{androidPurchase.OrderId}
                        ProductId:{androidPurchase.ProductId}
                        Quantity:{androidPurchase.Quantity}
                        ObfuscatedAccountid:{androidPurchase.ObfuscatedAccountId}
                        PurchaseToken:{androidPurchase.PurchaseToken}
                    """);
                bool verifiedWithStore = false;
                int? verifiedAcknowledgementState = null;
                if (isSubscription)
                {
                    Google.Apis.AndroidPublisher.v3.Data.SubscriptionPurchase? verifiedPurchase = null;
                    try
                    {
                        verifiedPurchase = LicenseCheck.GetSubscriptionPurchase(
                            androidPurchase.PackageName, androidPurchase.ProductId, androidPurchase.PurchaseToken);
                        if (verifiedPurchase != null)
                        {
                            verifiedWithStore = true;
                            verifiedAcknowledgementState = verifiedPurchase.AcknowledgementState;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("In RecordPurchaseFunction, exception calling Google to check purchase:{0}", ex.Message);
                    }
                }
                else
                {
                    Google.Apis.AndroidPublisher.v3.Data.ProductPurchase? verifiedPurchase = null;
                    try
                    {
                        verifiedPurchase = LicenseCheck.GetProductPurchase(
                            androidPurchase.PackageName, androidPurchase.ProductId, androidPurchase.PurchaseToken);
                        if (verifiedPurchase != null)
                        {
                            verifiedWithStore = true;
                            verifiedAcknowledgementState = verifiedPurchase.AcknowledgementState;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("In RecordPurchaseFunction, exception calling Google to check purchase:{0}", ex.Message);
                    }
                }
                if (!verifiedWithStore)
                    logger.LogError("In RecordPurchaseFunction, could not verify purchase with Google");
                else if (verifiedAcknowledgementState == 0)
                    {
                        logger.LogInformation("In RecordPurchaseFunction, successfully verified unacknowledged purchase with Google, checking license table");
                        // All is well so far and we have a legitimately issued license which has not been acknowledged
                        // Now ensure it is not already known and if not, remember it for the future
                        bool recorded = await licenseStore.RecordAsync(androidPurchase.ProductId, androidPurchase.OrderId, androidPurchase.Quantity, androidPurchase.ObfuscatedAccountId);
                        return recorded;
                    }
                else
                    logger.LogError("In RecordPurchaseFunction, purchase already acknowledged AcknowledgementState=" + verifiedAcknowledgementState);

            }
            return false;
        }

        /// Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
        /// a the function code may be simultaneously executed on multiple threads.
        [Function("recordpurchase")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            logger.LogInformation("The 'recordpurchase' web service function is processing a request.");
            // Get the options passed in the query string
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            string subscription = query["subscription"] ?? "0";
            AndroidPurchase? androidPurchase = null;
            try
            {
                androidPurchase = await AndroidPurchase.FromJsonAsync(req.Body);
                logger.LogInformation($"successfully deserialized androidPurchase from request body");
                if (string.IsNullOrWhiteSpace(androidPurchase?.OrderId))
                    return await req.MakeResponseAsync(HttpStatusCode.BadRequest);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "In 'recordpurchase', Exception deserializing product");
                return await req.MakeResponseAsync(HttpStatusCode.BadRequest);
            }

            bool recorded = await RecordAsync(androidPurchase, isSubscription: subscription.Equals("1"));

            if (recorded)
                return await req.OkResponseAsync();
            else
                return await req.MakeResponseAsync(HttpStatusCode.BadRequest);
        }
    }
}
