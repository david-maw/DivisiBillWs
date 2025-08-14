using Microsoft.Azure.Functions.Worker.Middleware;
using System.Text.Json;

namespace DivisiBillWs;

/// <summary>
/// Middleware infrastructure and components
/// Based on https://adamstorr.co.uk/blog/conditional-middleware-in-isolated-azure-functions/
/// </summary>
public static class MiddleWare
{
    internal static bool IsTriggeredBy(this FunctionContext context, string triggerType)
        => context.FunctionDefinition.InputBindings.Values.First(a => a.Type.EndsWith("Trigger")).Type == triggerType;
}
/// <summary>
/// Base middleware class, allows easy declaration of child classes valid only for certain trigger types
/// </summary>
public abstract class TriggerMiddlewareBase(ILogger<TriggerMiddlewareBase> loggerParam) : IFunctionsWorkerMiddleware
{
    protected readonly ILogger logger = loggerParam;

    public abstract string TriggerType { get; }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next) =>
        // always run the next middleware if the current one is not applicable to the current trigger type.
        await (context.IsTriggeredBy(TriggerType) ? InnerInvoke(context, next) : next(context));

    protected abstract Task InnerInvoke(FunctionContext context, FunctionExecutionDelegate next);
}
/// <summary>
/// Specialization for HTTP triggers, exists for convenience
/// </summary>
public abstract class HttpTriggerMiddlewareBase(ILogger<HttpTriggerMiddlewareBase> loggerParam) : TriggerMiddlewareBase(loggerParam)
{
    public override string TriggerType => "httpTrigger";
}
/// <summary>
/// A concrete class designed to catch and report exceptions in azure functions
/// </summary>
public class CustomExceptionHandler(ILogger<CustomExceptionHandler> loggerParam) : HttpTriggerMiddlewareBase(loggerParam)
{
    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    protected override async Task InnerInvoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var request = await context.GetHttpRequestDataAsync();
            // create a response to the request (since the callee may not have done so because it threw an exception)
            var response = request!.CreateResponse();
            response.StatusCode = HttpStatusCode.InternalServerError; // return an error code to the caller
            var errorMessage = new { Function = context.FunctionDefinition.Name, Message = "An unhandled exception occurred. Please try again later", Exception = ex.Message };
            string responseBody = JsonSerializer.Serialize(errorMessage, jsonOptions);
            await response.WriteStringAsync(responseBody);
            context.GetInvocationResult().Value = response;
            logger.LogError(ex, "Exception Thrown Invoking '{FunctionName}'", context.FunctionDefinition.Name);
        }
    }
}
/// <summary>
/// A concrete class to authenticate most functions with specific exceptions
/// </summary>
public class AuthenticationMiddleware : HttpTriggerMiddlewareBase
{
    public AuthenticationMiddleware(ILogger<AuthenticationMiddleware> loggerParam) : base(loggerParam)
    {
        licenseStore = new LicenseStore(logger);
        storage = new(logger, licenseStore);
        authorization = new(logger, licenseStore);
    }

    internal readonly DataStore<MealStorage> storage;
    private readonly Authorization authorization;
    private readonly LicenseStore licenseStore;

    protected override async Task InnerInvoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        string functionName = context.FunctionDefinition.Name;

        bool needsVerification = functionName switch
        {
            "version" or "scan" or "verify" or "recordpurchase" => false,
            _ => true,
        };

        if (needsVerification)
        {
            var httpContext = context.GetHttpContext();
            if (httpContext == null)
            {
                logger.LogError("In AuthenticateMiddleware, httpContext is null");
                return;
            }
            // Now do the heavy lifting of actual authentication
            string? userKey = await authorization.GetAuthorizedUserKeyAsync(httpContext.Request);
            if (userKey == null)
            {
                logger.LogError("In AuthenticateMiddleware for {FunctionName}, DivisiBill authorization failed, returning BadRequest", functionName);
                httpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }
            else
            {
                httpContext.Items["userKey"] = userKey;
                // Check to see if the token has expired, or is about to, so you can send a new one back to the caller if necessary
                string? token = licenseStore.GetTokenIfNew(userKey);
                if (token != null)
                { // The token has been updated, so add it to the response headers so the caller can use it next time they need to call us
                    logger.LogInformation("In AuthenticationMiddleware, called licenseStore.GetTokenIfNew, returned {TokenStatus}", token is null ? "null" : "value");
                    httpContext.Response.Headers[Authorization.TokenHeaderName] = token;
                }
                logger.LogInformation("In AuthenticateMiddleware for {FunctionName}, DivisiBill authorization succeeded", functionName);
            }
        }
        else
            logger.LogInformation("In AuthenticateMiddleware for {FunctionName}, DivisiBill authorization not required", functionName);
        await next(context);
    }
}
