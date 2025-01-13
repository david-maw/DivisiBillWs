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
    public static SubscriptionPurchaseV2? GetSubscriptionPurchase(string packageName, string productId, string token)
    {
        ServiceAccountCredential? serviceAccountCredential = GetServiceAccountCredential();

        if (serviceAccountCredential == null)
            return null;
        else
        {
            // Create the service.
            var service = new AndroidPublisherService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = serviceAccountCredential,
                ApplicationName = "DivisiBillWs",
            });
            return service.Purchases.Subscriptionsv2.Get(packageName, token).Execute();
        }
    }
    public static ProductPurchase? GetProductPurchase(string packageName, string productId, string token)
    {
        ServiceAccountCredential? serviceAccountCredential = GetServiceAccountCredential();

        if (serviceAccountCredential == null)
            return null;
        else
        {
            // Create the service.
            var service = new AndroidPublisherService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = serviceAccountCredential,
                ApplicationName = "DivisiBillWs",
            });
            return service.Purchases.Products.Get(packageName, productId, token).Execute();
        }
    }
}
