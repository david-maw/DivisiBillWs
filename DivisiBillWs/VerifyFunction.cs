using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DivisiBillWs;

public partial class VerifyFunction
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

    // Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
    // a the function code may be simultaneously executed on multiple threads.
    [Function(nameof(VerifyAndroidPurchase))]
    public Task<IActionResult> VerifyAndroidPurchase([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest httpRequest)
    {
        LogVerifyAndroidPurchaseProcessing(nameof(VerifyAndroidPurchase));

        return authorization.VerifyAndroidPurchase(httpRequest);
    }

    [LoggerMessage(LogLevel.Information, "The '{name}' web service is processing a request.")]
    private partial void LogVerifyAndroidPurchaseProcessing(string name);
}
