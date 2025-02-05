using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Json;
using Google.Apis.Services;

namespace DivisiBillWs;

internal static class LicenseCheck
{
    private static ServiceAccountCredential? GetServiceAccountCredential()
    {
        if (string.IsNullOrEmpty(Generated.BuildInfo.PlayCredentialB64))
            return null;
        string json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Generated.BuildInfo.PlayCredentialB64));
        JsonCredentialParameters parameters = NewtonsoftJsonSerializer.Instance.Deserialize<JsonCredentialParameters>(json);

        // Create a credential initializer with the correct scopes.
        ServiceAccountCredential.Initializer initializer =
            new(parameters.ClientEmail)
            {
                Scopes = [AndroidPublisherService.Scope.Androidpublisher]
            };

        // Create a service account credential object using the deserialized private key.
        ServiceAccountCredential credential =
            new(initializer.FromPrivateKey(parameters.PrivateKey));

        return credential;
    }
    private static AndroidPublisherService GetAndroidPublisherService()
    {
        ServiceAccountCredential? serviceAccountCredential = GetServiceAccountCredential();
        return serviceAccountCredential == null
            ? throw new System.Exception("No service account credential")
            : new AndroidPublisherService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = serviceAccountCredential,
                ApplicationName = "DivisiBillWs",
            });
    }
    public static SubscriptionPurchaseV2? GetSubscriptionPurchase(string packageName, string productId, string token) =>
        GetAndroidPublisherService().Purchases.Subscriptionsv2.Get(packageName, token).Execute();
    public static ProductPurchase? GetProductPurchase(string packageName, string productId, string token) =>
        GetAndroidPublisherService().Purchases.Products.Get(packageName, productId, token).Execute();
    public static void AcknowledgeProductPurchase(string packageName, string productId, string token) =>
        GetAndroidPublisherService().Purchases.Products.Acknowledge(new ProductPurchasesAcknowledgeRequest(), packageName, productId, token).Execute();
    public static void AcknowledgeSubscriptionPurchase(string packageName, string productId, string token) =>
        GetAndroidPublisherService().Purchases.Subscriptions.Acknowledge(new SubscriptionPurchasesAcknowledgeRequest(), packageName, productId, token).Execute();
}
