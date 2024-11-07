using System.Text.Json;

namespace DivisiBillWs
{
    public class VerifyFunction
    {
        private readonly ILogger logger;

        public VerifyFunction(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger<VerifyFunction>();
            licenseStore = new LicenseStore(logger);
            authorization = new(logger, licenseStore);
        }

        Authorization authorization;

        LicenseStore licenseStore;

        /// Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
        /// a the function code may be simultaneously executed on multiple threads.
        [Function("verify")]
        public Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            logger.LogInformation("The 'verify' web service is processing a request.");
            // Get the options passed in the query string
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            string subscription = query["subscription"] ?? "0";

            return authorization.GetIsVerifiedAsync(req, isSubscription: subscription.Equals("1"));
        }
    }
}
