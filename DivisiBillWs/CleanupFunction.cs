using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DivisiBillWs;

public partial class CleanupFunction
{
    private readonly ILogger logger;
    internal readonly DataStore<VenueListStorage> venueListStorage;
    internal readonly DataStore<PersonListStorage> personListStorage;
    internal readonly BlobContainerClient imagesBlobContainer;
    public CleanupFunction(ILoggerFactory loggerFactory, BlobContainerClient imagesBlobContainerParam)
    {
        logger = loggerFactory.CreateLogger<CleanupFunction>();
        venueListStorage = new(logger, null);
        personListStorage = new(logger, null);
        imagesBlobContainer = imagesBlobContainerParam;
    }

    [Function("CleanupFunction")]
    // Run at 9 am every Wednesday or when manually triggered via HTTP DELETE request
    public async Task Run([TimerTrigger("0 0 9 * * Wed")] TimerInfo myTimer)
    {
        LogCleanupFunctionExecuted(DateTime.Now);

        if (myTimer.ScheduleStatus is not null)
        {
            LogCleanupFunctionNextSchedule(myTimer.ScheduleStatus.Next);
        }
        await venueListStorage.CleanupAllUsersAsync();
        await personListStorage.CleanupAllUsersAsync();
        await CleanupDeletedImages();
    }

    [Function("ManualCleanup")]
    // Run at 9 am every Wednesday or when manually triggered via HTTP DELETE request
    public async Task<IActionResult> RunManual([HttpTrigger(AuthorizationLevel.Function, "delete")] HttpRequest req)
    {
        LogManualCleanupExecuted();

        await venueListStorage.CleanupAllUsersAsync();
        await personListStorage.CleanupAllUsersAsync();
        await CleanupDeletedImages();
        return new OkObjectResult("Manual cleanup completed.");
    }

    /// <summary>
    /// Cleans up deleted images from the Azure Blob Storage container for Images. Candidate blobs for deletion
    /// are those that were created 90 or more days ago and whose names begin with 'deleted/'. This method iterates through
    /// the blobs in the container, checks to see if they meet these criteria, and deletes them if they do.
    /// </summary>
    /// <returns></returns>
    private async Task CleanupDeletedImages()
    {
        LogCleanupDeletedImagesExecuted();
        if (imagesBlobContainer == null)
            return;

        try
        {
            string yyymmddhhmmssNinetyDaysAgo = DateTime.Now.AddDays(-90).ToString("yyyyMMddHHmmss");
            var blobItems = imagesBlobContainer.GetBlobsAsync();
            string prefixToIgnore = "";
            string priorPrefix = "";
            // This will return all the blobs; we just consider the ones in "deleted/" and check if they are older than 90 days
            await foreach (var blobItem in blobItems.Where(b => b.Name.StartsWith("deleted/")))
            {
                string prefix = Path.GetDirectoryName(blobItem.Name) ?? ""; // There must be at least the prefix "deleted/" in the blob name, so this will never be null
                if (!string.Equals(prefix, priorPrefix))
                {
                    LogCheckingBlobs(prefix);
                    priorPrefix = prefix;
                }
                if (string.Equals(prefix, prefixToIgnore))
                    continue;
                var blobName = Path.GetFileName(blobItem.Name);
                if (string.CompareOrdinal(blobName, yyymmddhhmmssNinetyDaysAgo) > 0)
                {
                    prefixToIgnore = prefix;
                    LogIgnoringRemainingBlobs(prefixToIgnore, blobName, yyymmddhhmmssNinetyDaysAgo);
                    continue;
                }
                var blobClient = imagesBlobContainer.GetBlobClient(blobItem.Name);
                await blobClient.DeleteIfExistsAsync();
                LogDeletedBlob(blobName);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred while cleaning up deleted images.");
        }
    }

    [LoggerMessage(LogLevel.Information, "CleanupFunction executed at: {executionTime}")]
    private partial void LogCleanupFunctionExecuted(DateTime executionTime);

    [LoggerMessage(LogLevel.Information, "ManualCleanup executed")]
    private partial void LogManualCleanupExecuted();

    [LoggerMessage(LogLevel.Information, "In CleanupFunction, next timer schedule at: {nextSchedule}")]
    private partial void LogCleanupFunctionNextSchedule(DateTime nextSchedule);

    [LoggerMessage(LogLevel.Information, "CleanupDeletedImages executed")]
    private partial void LogCleanupDeletedImagesExecuted();

    [LoggerMessage(LogLevel.Information, "Checking blobs for {prefix}.")]
    private partial void LogCheckingBlobs(string prefix);

    [LoggerMessage(LogLevel.Information, "Ignoring remaining blobs for {prefixToIgnore} as {blobName} is not older than 90 days ({yyymmddhhmmssNinetyDaysAgo}).")]
    private partial void LogIgnoringRemainingBlobs(string prefixToIgnore, string blobName, string yyymmddhhmmssNinetyDaysAgo);

    [LoggerMessage(LogLevel.Information, "Deleted blob: {blobName}")]
    private partial void LogDeletedBlob(string blobName);
}