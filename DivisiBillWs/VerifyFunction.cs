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

    /// Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
    /// a the function code may be simultaneously executed on multiple threads.
    [Function("verify")]
    public Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest httpRequest)
    {
        logger.LogInformation("The 'verify' web service is processing a request.");
        // Get the options passed in the query string
        var query = httpRequest.Query;
        string subscription = query["subscription"].ToString() ?? "0";

        return authorization.GetIsVerifiedAsync(httpRequest, isSubscription: subscription.Equals("1"));
    }
}
