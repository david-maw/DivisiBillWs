using Azure.Storage.Blobs;
using DivisiBillWs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sentry.Azure.Functions.Worker;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication((host, builder) =>
    {
        builder.UseMiddleware<CustomExceptionHandler>();
        builder.UseMiddleware<AuthenticationMiddleware>();
        builder.UseSentry(host, options =>
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
    })
    .Build();

host.Run();
