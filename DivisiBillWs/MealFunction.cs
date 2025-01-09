using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DivisiBillWs;

public class MealFunction
{
    private readonly ILogger logger;
    internal readonly DataStore<MealStorage> storage;

    public MealFunction(ILoggerFactory loggerFactory)
    {
        logger = loggerFactory.CreateLogger<MealFunction>();
        licenseStore = new LicenseStore(logger);
        storage = new(logger, licenseStore);
        authorization = new(logger, licenseStore);
    }

    private readonly Authorization authorization;
    private readonly LicenseStore licenseStore;

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
        logger.LogInformation($"MealFunction HTTP trigger function processing a {httpRequest.Method} request for id {id}");

        string? userKey = await authorization.GetAuthorizedUserKeyAsync(httpRequest);
        if (userKey == null)
        {
            logger.LogInformation($"MealFunction authorization failed, returning BadRequest");
            return new BadRequestResult();
        }

        // Authorized, so call the appropriate function
        Task<IActionResult> actionResult = httpRequest.Method switch
        {
            "PUT" => storage.PutAsync(httpRequest, userKey, id),
            "GET" => storage.GetAsync(httpRequest, userKey, id),
            "DELETE" => storage.DeleteAsync(httpRequest, userKey, id),
            _ => throw new ArgumentOutOfRangeException(httpRequest.Method),
        };

        return await actionResult;
    }
    [Function("Meals")]
    public async Task<IActionResult> Enumerate([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest httpRequest)
    {
        logger.LogInformation("Meals function processing a request.");

        string? userKey = await authorization.GetAuthorizedUserKeyAsync(httpRequest);
        if (userKey == null)
        {
            logger.LogInformation($"Meals authorization failed, returning error");
            return new BadRequestResult();
        }

        // Now do the actual work
        return await storage.EnumerateAsync(httpRequest, userKey);
    }
}
