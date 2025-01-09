using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DivisiBillWs;

public class VenueListFunction
{
    private readonly ILogger logger;
    internal readonly DataStore<VenueListStorage> storage;

    public VenueListFunction(ILoggerFactory loggerFactory)
    {
        logger = loggerFactory.CreateLogger<VenueListFunction>();
        licenseStore = new LicenseStore(logger);
        storage = new(logger, licenseStore);
        authorization = new(logger, licenseStore);
    }

    private readonly Authorization authorization;
    private readonly LicenseStore licenseStore;

    /// <summary>
    /// CRUD for a single VenueList. Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
    /// a the function code may be simultaneously executed on multiple threads.
    /// </summary>
    /// <param name="req">The incoming HTTP request </param>
    /// <param name="id">The name of the item we are addressing</param>
    /// <returns>An HTTP response and possibly the data associated with the named item</returns>
    [Function("VenueListFunction")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "put", "delete",
        Route = "venuelist/{id}")] HttpRequest req, string id)
    {
        logger.LogInformation($"VenueListFunction HTTP trigger function processing a {req.Method} request for id {id}");

        string? userKey = await authorization.GetAuthorizedUserKeyAsync(req);
        if (userKey == null)
        {
            logger.LogInformation($"VenueListFunction authorization failed, returning BadRequest");
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
    [Function("VenueLists")]
    public async Task<IActionResult> Enumerate([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequest req)
    {
        logger.LogInformation("VenueLists function processing a request.");

        string? userKey = await authorization.GetAuthorizedUserKeyAsync(req);
        if (userKey == null)
        {
            logger.LogInformation($"VenueLists authorization failed, returning error");
            return new BadRequestResult();
        }
        // Now do the actual work
        return await storage.EnumerateAsync(req, userKey);
    }
}
