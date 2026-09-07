using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DivisiBillWs;

public partial class MealFunction
{
    private readonly ILogger logger;
    internal readonly DataStore<MealStorage> storage;
    private readonly LicenseStore licenseStore;

    public MealFunction(ILoggerFactory loggerFactory)
    {
        logger = loggerFactory.CreateLogger<MealFunction>();
        licenseStore = new LicenseStore(logger);
        storage = new(logger, licenseStore);
    }

    /// <summary>
    /// CRUD for a single meal. Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
    /// a the function code may be simultaneously executed on multiple threads.
    /// </summary>
    /// <param name="httpRequest">The incoming HTTP request </param>
    /// <param name="id">The name of the item we are addressing</param>
    /// <returns>An HTTP response and possibly the data associated with the named item</returns>
    [Function("MealFunction")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "put", "delete",
        Route = "meal/{id}")] HttpRequest httpRequest, string id)
    {
        LogMealFunctionProcessing(httpRequest.Method, id);
        string userKey = httpRequest.HttpContext.Items["userKey"] as string
            ?? throw new NullReferenceException("userKey");
        // Already authorized, so call the appropriate function
        Task<IActionResult> actionResult = httpRequest.Method switch
        {
            "PUT" => storage.PutAsync(httpRequest, userKey, id),
            "GET" => storage.GetAsync(userKey, id),
            "DELETE" => storage.DeleteAsync(userKey, id),
            _ => throw new ArgumentOutOfRangeException(httpRequest.Method),
        };

        return await actionResult;
    }

    [Function("Meals")]
    public async Task<IActionResult> Enumerate([HttpTrigger(AuthorizationLevel.Function, "get", "delete")] HttpRequest httpRequest)
    {
        LogMealsProcessing(httpRequest.Method);
        Task<IActionResult> actionResult = httpRequest.Method switch
        {
            "GET" => storage.EnumerateAsync(httpRequest),
            "DELETE" => storage.DeleteAllAsync(httpRequest),
            _ => throw new ApplicationException($"Unknown HTTP method '{httpRequest.Method}'")
        };
        return await actionResult;
    }

    [LoggerMessage(LogLevel.Information, "MealFunction HTTP trigger function processing a {Method} request for ID {Id}")]
    private partial void LogMealFunctionProcessing(string Method, string Id);

    [LoggerMessage(LogLevel.Information, "Meals function processing a {method} request.")]
    private partial void LogMealsProcessing(string method);
}
