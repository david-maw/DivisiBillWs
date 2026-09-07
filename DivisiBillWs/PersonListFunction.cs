using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DivisiBillWs;

public partial class PersonListFunction
{
    private readonly ILogger logger;
    internal readonly DataStore<PersonListStorage> storage;

    public PersonListFunction(ILoggerFactory loggerFactory)
    {
        logger = loggerFactory.CreateLogger<PersonListFunction>();
        licenseStore = new LicenseStore(logger);
        storage = new(logger, licenseStore);
        authorization = new(logger, licenseStore);
    }

    private readonly Authorization authorization;
    private readonly LicenseStore licenseStore;

    /// <summary>
    /// CRUD for a single PersonList. Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
    /// a the function code may be simultaneously executed on multiple threads.
    /// </summary>
    /// <param name="httpRequest">The incoming HTTP request </param>
    /// <param name="id">The name of the item we are addressing</param>
    /// <returns>An HTTP response and possibly the data associated with the named item</returns>
    [Function("PersonListFunction")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "put", "delete",
    Route = "personlist/{id}")] HttpRequest httpRequest, string id)
    {
        LogPersonListFunctionProcessing(httpRequest.Method, id);
        if (httpRequest.HttpContext.Items["userKey"] is not string userKey)
            return new UnauthorizedResult(); // Should never happen because the middleware takes care of this
        // Already authorized, so call the appropriate function
        Task<IActionResult> actionResult = httpRequest.Method switch
        {
            "PUT" => storage.PutAsync(httpRequest, userKey, id),
            "GET" => storage.GetAsync(userKey, id),
            "DELETE" => storage.DeleteAsync(userKey, id),
            _ => throw new ApplicationException($"Unknown HTTP method '{httpRequest.Method}'")
        };

        return await actionResult;
    }
    [Function("PersonLists")]
    public async Task<IActionResult> EnumerateAsync([HttpTrigger(AuthorizationLevel.Function, "get", "delete")] HttpRequest httpRequest)
    {
        LogPersonListsProcessing(httpRequest.Method);
        Task<IActionResult> actionResult = httpRequest.Method switch
        {
            "GET" => storage.EnumerateAsync(httpRequest),
            "DELETE" => storage.DeleteAllAsync(httpRequest),
            _ => throw new ApplicationException($"Unknown HTTP method '{httpRequest.Method}'")
        };
        return await actionResult;
    }

    [LoggerMessage(LogLevel.Information, "PersonListFunction HTTP trigger function processing a {method} request for ID {id}")]
    private partial void LogPersonListFunctionProcessing(string method, string id);

    [LoggerMessage(LogLevel.Information, "PersonLists function processing a {method} request.")]
    private partial void LogPersonListsProcessing(string method);
}
