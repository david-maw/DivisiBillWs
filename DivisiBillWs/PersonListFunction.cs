using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DivisiBillWs;

public class PersonListFunction
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
        logger.LogInformation($"PersonListFunction HTTP trigger function processing a {httpRequest.Method} request for ID {id}");
        string? userKey = httpRequest.HttpContext.Items["userKey"] as string;
        // Already authorized, so call the appropriate function
        Task<IActionResult> actionResult = httpRequest.Method switch
        {
            "PUT" => storage.PutAsync(httpRequest, userKey, id),
            "GET" => storage.GetAsync(httpRequest, userKey, id),
            "DELETE" => storage.DeleteAsync(httpRequest, userKey, id),
            _ => throw new ApplicationException($"Unknown HTTP method '{httpRequest.Method}'")
        };

        return await actionResult;
    }
    [Function("PersonLists")]
    public async Task<IActionResult> EnumerateAsync([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest httpRequest)
    {
        logger.LogInformation("PersonLists function processing a request.");
        // Now do the actual work
        return await storage.EnumerateAsync(httpRequest);
    }
}
