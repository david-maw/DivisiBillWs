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
    /// <param name="req">The incoming HTTP request </param>
    /// <param name="id">The name of the item we are addressing</param>
    /// <returns>An HTTP response and possibly the data associated with the named item</returns>
    [Function("PersonListFunction")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "put", "delete",
    Route = "personlist/{id}")] HttpRequest req, string id)
    {
        logger.LogInformation($"PersonListFunction HTTP trigger function processing a {req.Method} request for ID {id}");

        string? userKey = await authorization.GetAuthorizedUserKeyAsync(req);
        if (userKey == null)
        {
            logger.LogInformation($"MealFunction authorization failed, returning BadRequest");
            return new BadRequestResult();
        }

        // Authorized, so call the appropriate function
        Task<IActionResult> actionResult = req.Method switch
        {
            "PUT" => storage.PutAsync(req, userKey, id),
            "GET" => storage.GetAsync(req, userKey, id),
            "DELETE" => storage.DeleteAsync(req, userKey, id),
            _ => throw new ApplicationException($"Unknown HTTP method '{req.Method}'")
        };

        return await actionResult;
    }
    [Function("PersonLists")]
    public async Task<IActionResult> EnumerateAsync([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
    {
        logger.LogInformation("PersonLists function processing a request.");

        string? userKey = await authorization.GetAuthorizedUserKeyAsync(req);
        if (userKey == null)
        {
            logger.LogInformation($"Meals authorization failed, returning error");
            return new BadRequestResult();
        }
        // Now do the actual work
        return await storage.EnumerateAsync(req, userKey);
    }
}
