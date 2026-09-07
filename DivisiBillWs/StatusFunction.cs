using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DivisiBillWs;

public partial class StatusFunction(ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<StatusFunction>();

    /// <summary>
    /// Returns the OCR license scans count and version level as JSON
    /// </summary>
    /// <param name="httpRequest">The incoming HTTP request</param>
    /// <param name="id">The integer parameter for status lookup</param>
    /// <returns>A JSON object containing OcrLicenseScans and version level</returns>
    [Function(nameof(Status))]
    public IActionResult Status(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "status/{id}")] HttpRequest _,
        string id)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            LogStatusFunctionProcessing(id);

        var response = new
        {
            StatusId = id,
            ResponseLevel = 1,
            ApplicationVersion = typeof(VersionFunction).Assembly.GetName().Version,
            LicenseStore.OcrLicenseScans
        };

        return new OkObjectResult(response);
    }

    [LoggerMessage(LogLevel.Information, "status function processing request with id: {Id}")]
    private partial void LogStatusFunctionProcessing(string id);
}
