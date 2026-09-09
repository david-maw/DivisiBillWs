using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DivisiBillWs;

public partial class RecordPurchaseFunction
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
            LogDeserializationError(logger);
        else if (string.IsNullOrEmpty(androidPurchase.PackageName))
            LogMissingProperty(logger, nameof(AndroidPurchase.PackageName));
        else if (!androidPurchase.PackageName.Equals(LicenseStore.ExpectedPackageName)) // only DivisiBill Licenses can be used
            LogInvalidPackageName(logger, androidPurchase.PackageName);
        else if (string.IsNullOrEmpty(androidPurchase.ProductId))
            LogMissingProperty(logger, nameof(AndroidPurchase.ProductId));
        else if (string.IsNullOrEmpty(androidPurchase.OrderId))
            LogMissingProperty(logger, nameof(AndroidPurchase.OrderId));
        else if (string.IsNullOrEmpty(androidPurchase.ObfuscatedAccountId))
            LogMissingProperty(logger, nameof(AndroidPurchase.ObfuscatedAccountId));
        else if (string.IsNullOrEmpty(androidPurchase.PurchaseToken))
            LogMissingProperty(logger, nameof(AndroidPurchase.PurchaseToken));
        else
        {
            LogRecordPurchaseDetails(logger, androidPurchase.PackageName, androidPurchase.OrderId, androidPurchase.ProductId, androidPurchase.Quantity, androidPurchase.ObfuscatedAccountId, androidPurchase.PurchaseToken);
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
                    LogGoogleVerificationError(logger, ex.Message);
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
                    LogGoogleVerificationError(logger, ex.Message);
                }
            }
            if (verifiedWithStore)
            {
                LogPurchaseVerified(logger, verifiedAcknowledgementState == 1 ? "acknowledged" : "unacknowledged");
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
                LogPurchaseVerificationFailed(logger);
        }
        return false;
    }

    /// Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
    /// a the function code may be simultaneously executed on multiple threads.
    [Function(nameof(RecordAndroidPurchase))]
    public async Task<IActionResult> RecordAndroidPurchase([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest httpRequest)
    {
        LogRecordAndroidPurchaseProcessing();
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

            LogAndroidPurchaseDeserialized();

            if (string.IsNullOrWhiteSpace(androidPurchase?.OrderId))
                return new BadRequestObjectResult("OrderId not found");

            if (string.IsNullOrWhiteSpace(androidPurchase?.ObfuscatedAccountId))
                return new BadRequestObjectResult("ObfuscatedAccountId not found");
        }
        catch (Exception ex)
        {
            LogRecordPurchaseDeserializationError(ex);
            return new BadRequestObjectResult("Error deserializing purchase");

        }

        bool recorded = await RecordAsync(androidPurchase, logger, licenseStore);

        return recorded
            ? new OkResult()
            : new BadRequestObjectResult("Failed to record purchase");
    }

    [LoggerMessage(LogLevel.Error, "In RecordPurchaseFunction, could not deserialize a purchase with an OrderId")]
    private static partial void LogDeserializationError(ILogger logger);

    [LoggerMessage(LogLevel.Error, "In RecordPurchaseFunction, could not extract a {PropertyName}")]
    private static partial void LogMissingProperty(ILogger logger, string PropertyName);

    [LoggerMessage(LogLevel.Error, "In RecordPurchaseFunction, package name was not com.autoplus.divisibill: {PackageName}")]
    private static partial void LogInvalidPackageName(ILogger logger, string PackageName);

    [LoggerMessage(LogLevel.Information, "In RecordPurchaseFunction, PackageName:{PackageName}, OrderId:{OrderId}, ProductId:{ProductId}, Quantity:{Quantity}, ObfuscatedAccountid:{ObfuscatedAccountId}, PurchaseToken:{PurchaseToken}")]
    private static partial void LogRecordPurchaseDetails(ILogger logger, string PackageName, string OrderId, string ProductId, int Quantity, string ObfuscatedAccountId, string PurchaseToken);

    [LoggerMessage(LogLevel.Error, "In RecordPurchaseFunction, exception calling Google to check purchase: {ExceptionMessage}")]
    private static partial void LogGoogleVerificationError(ILogger logger, string ExceptionMessage);

    [LoggerMessage(LogLevel.Information, "In RecordPurchaseFunction, successfully verified {AcknowledgementState} purchase with Google, checking license table")]
    private static partial void LogPurchaseVerified(ILogger logger, string AcknowledgementState);

    [LoggerMessage(LogLevel.Error, "In RecordPurchaseFunction, could not verify purchase with Google")]
    private static partial void LogPurchaseVerificationFailed(ILogger logger);

    [LoggerMessage(LogLevel.Information, "The '{name}' web service function is processing a request.")]
    private partial void LogRecordAndroidPurchaseProcessing(string name = nameof(RecordAndroidPurchase));

    [LoggerMessage(LogLevel.Information, "successfully deserialized androidPurchase from request")]
    private partial void LogAndroidPurchaseDeserialized();

    [LoggerMessage(LogLevel.Error, "In 'recordpurchase', Exception deserializing product")]
    private partial void LogRecordPurchaseDeserializationError(Exception ex);
}
