using DivisiBillWs.Generated;
using Microsoft.Extensions.Hosting;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace DivisiBillWs
{
    public class VersionFunction
    {
        private readonly ILogger _logger;
        private readonly IHostEnvironment env;
        private readonly bool IsDebug =
        #if DEBUG
                true;
#else
                false;
#endif

        public VersionFunction(ILoggerFactory loggerFactory, IHostEnvironment envParam)
        {
            _logger = loggerFactory.CreateLogger<VersionFunction>();
            env = envParam;
        }
        public static string BuildTime { get; } = DateTime.Parse(BuildEnvironment.BuildTimeString, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime().ToString();

        /// Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
        /// a the function code may be simultaneously executed on multiple threads.
        [Function("version")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequestData req)
        {
            _logger.LogInformation("The 'version' web service function is processing a request.");

            return await req.MakeResponseAsync(HttpStatusCode.OK,$""" 
                Application: {env.ApplicationName} 
                Application_Version: {typeof(VersionFunction).Assembly.GetName().Version}
                NET_Version: {typeof(int).Assembly.GetName().Version}
                Build_Time: {BuildTime}
                Environment: {env.EnvironmentName}
                Platform:{Environment.OSVersion.Platform}
                Content_Path: {env.ContentRootPath}
                Debug: {IsDebug}
                Site_Name: {Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")}
                Play_Store_Key: {(string.IsNullOrEmpty(Generated.BuildInfo.PlayCredentialB64) ? "Missing" : "Present")} 
                Cognitive_Services_Entry_Point: {(string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillCognitiveServicesEndpoint) ? "Missing" : "Present")} 
                Cognitive_Services_Key: {(string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillCognitiveServicesKey) ? "Missing" : "Present")} 
                Sentry_DSN: {(string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillSentryDsn) ? "Missing" : "Present")} 
                """);
        }
    }
}
