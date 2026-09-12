using Azure.Storage.Blobs;
using DivisiBillWs;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sentry.Azure.Functions.Worker;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

//if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
//{
//    builder.Services.AddOpenTelemetry()
//        .UseFunctionsWorkerDefaults()
//        .UseAzureMonitorExporter();
//}
builder.UseMiddleware<CustomExceptionHandler>();
builder.UseMiddleware<AuthenticationMiddleware>();

builder.UseSentry(options =>
    {
        options.Dsn = DivisiBillWs.Generated.BuildInfo.DivisiBillSentryDsn;
        options.SetBeforeSend(sentryEvent => DivisiBillWs.Utility.IsDebug ? null : sentryEvent);
        //  Other options to consider
        //    options.Release = Utilities.VersionName;
        //    options.Environment = App.IsDebug ? "debug" : "production";
        //    options.AddTransactionProcessor(new Services.SentryTransactionProcessor());
        //    // Set TracesSampleRate to 1.0 to capture 100% of transactions for performance monitoring.
        //    // We recommend adjusting this value in production.
        //    options.TracesSampleRate = 1.0;
        //    // Sample rate for profiling, applied on top of the TracesSampleRate,
        //    // e.g. 0.2 means we want to profile 20 % of the captured transactions.
        //    // We recommend adjusting this value in production.
        //    options.ProfilesSampleRate = 1.0;
    });

builder.Services.AddSingleton(provider =>
{
    var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
    return new BlobContainerClient(connectionString, "images");
});

// Diagnostic logging if needed
//builder.Services.AddLogging(logging =>
//{
//    logging.AddConsole();
//    logging.AddDebug();
//});

// AzureEventSourceListener.CreateConsoleLogger(); // Use this to audit Azure SDK calls, but it will log a lot of information, so use it only for debugging purposes.

builder.Build().Run();
