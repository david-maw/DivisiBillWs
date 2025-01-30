﻿using Microsoft.Azure.Functions.Worker.Middleware;
using System.Text.Json;

namespace DivisiBillWs;

/// <summary>
/// Based on https://adamstorr.co.uk/blog/conditional-middleware-in-isolated-azure-functions/
/// </summary>
public static class MiddleWare
{
    internal static bool IsTriggeredBy(this FunctionContext context, string triggerType)
        => context.FunctionDefinition.InputBindings.Values.First(a => a.Type.EndsWith("Trigger")).Type == triggerType;
}
public abstract class TriggerMiddlewareBase : IFunctionsWorkerMiddleware
{
    public abstract string TriggerType { get; }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next) =>
        // always run the next middleware if the current one is not applicable to the current trigger type.
        await (context.IsTriggeredBy(TriggerType) ? InnerInvoke(context, next) : next(context));

    protected abstract Task InnerInvoke(FunctionContext context, FunctionExecutionDelegate next);
}
public abstract class HttpTriggerMiddlewareBase : TriggerMiddlewareBase
{
    public override string TriggerType => "httpTrigger";
}
public class CustomExceptionHandler : HttpTriggerMiddlewareBase
{
    protected override async Task InnerInvoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
            Console.WriteLine("No exception occurred");
        }
        catch (Exception ex)
        {
            var request = await context.GetHttpRequestDataAsync();
            // create a response to the request (since the callee may not have done so because it threw an exception)
            var response = request!.CreateResponse();
            response.StatusCode = HttpStatusCode.InternalServerError; // return an error code to the caller
            var errorMessage = new { Function = context.FunctionDefinition.Name, Message = "An unhandled exception occurred. Please try again later", Exception = ex.Message };
            string responseBody = JsonSerializer.Serialize(errorMessage, new JsonSerializerOptions { WriteIndented = true });
            await response.WriteStringAsync(responseBody);
            context.GetInvocationResult().Value = response;
            // Log the failure
            var log = context.GetLogger<CustomExceptionHandler>();
            log.LogError($"Exception Thrown Invoking '{context.FunctionDefinition.Name}'");
        }
    }
}
public class AuthenticationMiddleware : HttpTriggerMiddlewareBase
{
    public AuthenticationMiddleware(ILogger<AuthenticationMiddleware> loggerParam)
    {
        logger = loggerParam;
        licenseStore = new LicenseStore(logger);
        storage = new(logger, licenseStore);
        authorization = new(logger, licenseStore);
    }

    private readonly ILogger logger;
    internal readonly DataStore<MealStorage> storage;
    private readonly Authorization authorization;
    private readonly LicenseStore licenseStore;

    protected override async Task InnerInvoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var log = context.GetLogger<CustomExceptionHandler>();
        string functionName = context.FunctionDefinition.Name;

        bool needsVerification = true;
        needsVerification = functionName switch
        {
            "version" or "scan" or "verify" => false,
            _ => true,
        };
        if (needsVerification)
        {
            var httpContext = context.GetHttpContext();
            if (httpContext == null)
            {
                log.LogError("In AuthenticateMiddleware, httpContext is null");
                return;
            }
            string? userKey = await authorization.GetAuthorizedUserKeyAsync(httpContext.Request);
            if (userKey == null)
            {
                logger.LogError($"In AuthenticateMiddleware for {functionName} authorization failed, returning BadRequest");
                httpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }
        }
        await next(context);
    }
}