using DivisiBillWs.Generated; // The build time information  created by the msbuild of the project file
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace DivisiBillWs;

public class VersionFunction(ILoggerFactory loggerFactory, IHostEnvironment environmentParam)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<VersionFunction>();
    private readonly IHostEnvironment environment = environmentParam;
    private readonly bool IsDebug =
#if DEBUG
            true;
#else
            false;
#endif
    public static string BuildTime { get; } = DateTime.Parse(BuildEnvironment.BuildTimeString, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime().ToString();

    /// Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
    /// a the function code may be simultaneously executed on multiple threads.
    [Function("version")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest httpRequest)
    {
        _logger.LogInformation("The 'version' web service function is processing a request.");

        return new OkObjectResult($""" 
            Application: {environment.ApplicationName} 
            Application_Version: {typeof(VersionFunction).Assembly.GetName().Version}
            NET_Version: {typeof(int).Assembly.GetName().Version}
            Build_Time: {BuildTime}
            Environment: {environment.EnvironmentName}
            Platform:{Environment.OSVersion.Platform}
            Content_Path: {environment.ContentRootPath}
            Debug: {IsDebug}
            Site_Name: {Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")}
            Play_Store_Key: {(string.IsNullOrEmpty(Generated.BuildInfo.PlayCredentialB64) ? "Missing" : "Present")} 
            Cognitive_Services_Entry_Point: {(string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillCognitiveServicesEndpoint) ? "Missing" : "Present")} 
            Cognitive_Services_Key: {(string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillCognitiveServicesKey) ? "Missing" : "Present")} 
            Sentry_DSN: {(string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillSentryDsn) ? "Missing" : "Present")} 
            """);
    }
}
