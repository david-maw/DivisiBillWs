using Azure.Data.Tables;
using Azure;
using Microsoft.Extensions.Logging;
using Google.Apis.AndroidPublisher.v3.Data;

namespace DivisiBillWs;
internal class PlayStore
{
    /// <summary>
    /// Check whether a purchase is recognized by the Play Store and is known by us
    /// Store it if it is not known by us so it will be known in future.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="androidPurchase"></param>
    /// <param name="isSubscription"></param>
    /// <returns></returns>
    internal static bool VerifyPurchase(ILogger logger, AndroidPurchase? androidPurchase, bool isSubscription)
    {
        bool isPermittedTestOrderId = false;
        if (androidPurchase == null)
            logger.LogError("In VerifyPurchase, could not deserialize a purchase");
        else if (string.IsNullOrEmpty(androidPurchase.PackageName))
            logger.LogError("In VerifyPurchase, could not extract a " + nameof(AndroidPurchase.PackageName));
        else if (!androidPurchase.PackageName.Equals(LicenseStore.ExpectedPackageName)) // only DivisiBill Licenses can be used
            logger.LogError("In VerifyPurchase, package name was not com.autoplus.divisibill: " + androidPurchase.PackageName);
        else if (string.IsNullOrEmpty(androidPurchase.ProductId))
            logger.LogError("In VerifyPurchase, could not extract a " + nameof(AndroidPurchase.ProductId));
        else if (string.IsNullOrEmpty(androidPurchase.PurchaseToken))
            logger.LogError("In VerifyPurchase, could not extract a " + nameof(AndroidPurchase.PurchaseToken));
        else
        {
            logger.LogInformation($"""
                    In VerifyPurchase androidPurchase
                        OrderId:{androidPurchase.OrderId}
                        PackageName:{androidPurchase.PackageName}
                        ProductId:{androidPurchase.ProductId}
                        ObfuscatedAccountid:{androidPurchase.ObfuscatedAccountId}
                        PurchaseToken:{androidPurchase.PurchaseToken}
                    """);
            string? verifiedOrderId = null;
            int? verifiedAcknowledgementState = null;

#if DEBUG // permit a test orderid
            if (androidPurchase.OrderId != null && androidPurchase.OrderId.Equals("Fake-OrderId"))
                isPermittedTestOrderId = true;
#endif
            if (isSubscription)
            {
                SubscriptionPurchase? verifiedSubscriptionPurchase = null;
                try
                {
                    if (isPermittedTestOrderId)
                    {
                        verifiedSubscriptionPurchase = new()
                        {
                            OrderId = androidPurchase.OrderId,
                            AcknowledgementState = 1
                        };
                    }
                    else
                    {
                        verifiedSubscriptionPurchase = LicenseCheck.GetSubscriptionPurchase(
                            androidPurchase.PackageName, androidPurchase.ProductId, androidPurchase.PurchaseToken);
                        if (verifiedSubscriptionPurchase != null) 
                        {
                            verifiedOrderId = verifiedSubscriptionPurchase.OrderId;
                            verifiedAcknowledgementState = verifiedSubscriptionPurchase.AcknowledgementState;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("In VerifyPurchase, exception calling Google to check subscription:{0}", ex.Message);
                }
            }
            else
            {
                ProductPurchase? verifiedPurchase = null;
                try
                {
                    if (isPermittedTestOrderId)
                    {
                        verifiedPurchase = new()
                        {
                            ProductId = androidPurchase.ProductId,
                            OrderId = androidPurchase.OrderId,
                            AcknowledgementState = 1
                        };
                    }
                    else
                    {
                        verifiedPurchase = LicenseCheck.GetProductPurchase(
                            androidPurchase.PackageName, androidPurchase.ProductId, androidPurchase.PurchaseToken);
                        if (verifiedPurchase != null)
                        {
                            verifiedOrderId = verifiedPurchase.OrderId;
                            verifiedAcknowledgementState = verifiedPurchase.AcknowledgementState;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError("In VerifyPurchase, exception calling Google to check purchase:{0}", ex.Message);
                }
            }
            if (verifiedAcknowledgementState == null && verifiedOrderId == null)
                logger.LogError("In VerifyPurchase, could not verify purchase with Google");
            else
            {
                if (verifiedOrderId == null)
                    logger.LogError("In VerifyPurchase, purchase.OrderId is null");
                else if (isPermittedTestOrderId)
                {
                    logger.LogInformation("In VerifyPurchase, faking test order, not checking license table");
                    return true;
                }
                else
                {
                    logger.LogInformation($"In VerifyPurchase, successfully verified {(verifiedAcknowledgementState == 1 ? "acknowledged" : "unacknowledged")} purchase with Google");
                    return true;
                }
            }
        }
        return false;
    }
}
