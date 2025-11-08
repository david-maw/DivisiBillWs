using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DivisiBillWs;

public class VerifyFunction
{
    private readonly ILogger logger;

    public VerifyFunction(ILoggerFactory loggerFactory)
    {
        logger = loggerFactory.CreateLogger<VerifyFunction>();
        licenseStore = new LicenseStore(logger);
        authorization = new(logger, licenseStore);
    }

    private readonly Authorization authorization;
    private readonly LicenseStore licenseStore;

    // TODO: Delete this function once all clients are updated to store signatures
    /// Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
    /// a the function code may be simultaneously executed on multiple threads.
    [Function("verify")]
    public Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest httpRequest)
    {
        logger.LogInformation("The 'verify' web service is processing a request.");
        return authorization.GetIsVerifiedAsync(httpRequest);
    }

    // Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
    // a the function code may be simultaneously executed on multiple threads.
    [Function(nameof(VerifyAndroidPurchase))]
    public Task<IActionResult> VerifyAndroidPurchase([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest httpRequest)
    {
        logger.LogInformation("The '{name}' web service is processing a request.", nameof(VerifyAndroidPurchase));

        return authorization.VerifyAndroidPurchase(httpRequest);
    }
}
