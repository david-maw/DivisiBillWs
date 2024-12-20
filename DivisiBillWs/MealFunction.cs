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

    Authorization authorization;
    
    LicenseStore licenseStore;

    /// <summary>
    /// CRUD for a single meal. Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
    /// a the function code may be simultaneously executed on multiple threads.
    /// </summary>
    /// <param name="req">The incoming HTTP request </param>
    /// <param name="id">The name of the item we are addressing</param>
    /// <returns>An HTTP response and possibly the data associated with the named item</returns>
    [Function("MealFunction")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "get", "put", "delete",
        Route = "meal/{id}")] HttpRequestData req, string id)
    {
        logger.LogInformation($"MealFunction HTTP trigger function processing a {req.Method} request for id {id}");

        string? userKey = await authorization.GetAuthorizedUserKeyAsync(req);
        if (userKey == null)
        {
            logger.LogInformation($"MealFunction authorization failed, returning BadRequest");
            return await req.MakeResponseAsync(HttpStatusCode.BadRequest);
        }

        // Authorized, so call the appropriate function
        Task<HttpResponseData> httpResponseData = req.Method switch
        {
            "PUT" => storage.PutAsync(req, userKey, id),
            "GET" => storage.GetAsync(req, userKey, id),
            "DELETE" => storage.DeleteAsync(req, userKey, id),
            _ => req.MakeResponseAsync(HttpStatusCode.BadRequest),
        };

        return await httpResponseData;
    }
    [Function("Meals")]
    public async Task<HttpResponseData> Enumerate([HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequestData req)
    {
        logger.LogInformation("Meals function processing a request.");

        string? userKey = await authorization.GetAuthorizedUserKeyAsync(req);
        if (userKey == null)
        {
            logger.LogInformation($"Meals authorization failed, returning error");
            return await req.MakeResponseAsync(HttpStatusCode.BadRequest);
        }

        // Now do the actual work
        return await storage.EnumerateAsync(req, userKey);
    }
}
