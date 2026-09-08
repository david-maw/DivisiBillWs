using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DivisiBillWs;

public class ProPurchaseNotifyFunction(ILogger<ProPurchaseNotifyFunction> logger)
{
    private readonly LicenseStore licenseStore = new(logger);

    [Function("ProPurchaseNotify")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "delete", Route = "proPurchaseNotify/{userKey}")] HttpRequest httpRequest, string userKey)
    {
        // Get the OrderId we are disposing of
        var androidPurchase = await Authorization.ProLicenseFromRequestAsync(logger, httpRequest);
        if (string.IsNullOrEmpty(androidPurchase?.OrderId))
        {
            return new BadRequestObjectResult("Invalid purchase data, no OrderId in Purchase.");
        }
        string orderId = androidPurchase.OrderId;
        if (orderId == "GPA.3332-0658-5128-80451") // This is a test pro order
            (userKey, orderId) = (orderId, userKey); // Swap values of userKey and orderId for testing
        MigrationClass rename = new(logger);
        return await rename.RenameAsNeeded(orderId, userKey, httpRequest);
    }
}