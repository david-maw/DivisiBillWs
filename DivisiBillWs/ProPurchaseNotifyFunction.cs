using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace DivisiBillWs;

public class ProPurchaseNotifyFunction(ILogger<ProPurchaseNotifyFunction> logger, BlobContainerClient blobContainer)
{
    private readonly LicenseStore licenseStore = new(logger);

    [Function("ProPurchaseNotify")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "delete", Route = "proPurchaseNotify/{userKey}")] HttpRequest httpRequest, string userKey)
    {
        // Get the OrderId we are disposing of
        var androidPurchase = await Authorization.ProLicenseFromRequestAsync(logger, httpRequest);
        if (string.IsNullOrEmpty(androidPurchase?.OrderId))
        {
            return new BadRequestObjectResult("Invalid purchase data, no OrderId in Purchase.");
        }
        string orderId = androidPurchase.OrderId;
        //(userKey, orderId) = (orderId, userKey); // Swap values of userKey and orderId for testing 
        #region Migrate Blobs
        await blobContainer.CreateIfNotExistsAsync();
        var blobsForOrderId = await GetBlobsWithPrefixAsync(orderId, httpRequest.HttpContext.RequestAborted);
        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (var blob in blobsForOrderId)
        {
            string newBlobName = $"{userKey}{blob.Name[orderId.Length..]}";
            await RenameBlobAsync(blob.Name, newBlobName, httpRequest.HttpContext.RequestAborted);
        }
        stopwatch.Stop();
        #endregion
        #region Migrate Tables
        int migratedMealCount = await MigrateTableDataAsync<MealStorage>(orderId, userKey, httpRequest.HttpContext.RequestAborted);
        int migratedPersonListCount = await MigrateTableDataAsync<PersonListStorage>(orderId, userKey, httpRequest.HttpContext.RequestAborted);
        int migratedVenueListCount = await MigrateTableDataAsync<VenueListStorage>(orderId, userKey, httpRequest.HttpContext.RequestAborted);
        #endregion
        string resultMsg = Utility.IsDebug
            ? $"OrderId: {orderId} ({blobsForOrderId.Count} blobs)\nto UserKey: {userKey} in {stopwatch.ElapsedMilliseconds} ms,\n"
                + $"Migrated Meal Count: {migratedMealCount}, Migrated PersonList Count: {migratedPersonListCount}, Migrated VenueList Count: {migratedVenueListCount}"
            : $"Migrated Images: ({blobsForOrderId.Count} Migrated Meals: {migratedMealCount}, Migrated PersonLists: {migratedPersonListCount}, Migrated VenueLists: {migratedVenueListCount}";

        return new OkObjectResult(resultMsg);
    }
    #region Blob Handling
    public async Task<List<BlobItem>> GetBlobsWithPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var results = new List<BlobItem>();

        await foreach (var blob in blobContainer.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
            prefix: prefix,
            cancellationToken: cancellationToken))
        {
            results.Add(blob);
        }

        return results;
    }

    /// <summary>
    /// Safely renames a blob by copying then deleting the original.
    /// Guarantees no data loss even if delete fails.
    /// </summary>
    public async Task RenameBlobAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        var source = blobContainer.GetBlobClient(oldName);
        var target = blobContainer.GetBlobClient(newName);

        // 1. Start copy
        await target.StartCopyFromUriAsync(source.Uri, cancellationToken: cancellationToken);

        // 2. Wait for copy to complete
        BlobProperties props;
        do
        {
            await Task.Delay(10, cancellationToken);
            props = await target.GetPropertiesAsync(cancellationToken: cancellationToken);
        }
        while (props.CopyStatus == CopyStatus.Pending);

        if (props.CopyStatus != CopyStatus.Success)
            throw new InvalidOperationException(
                $"Blob rename failed: copy status = {props.CopyStatus}");

        // 3. Delete original (safe: if this fails, both blobs still exist)
        try
        {
            await source.DeleteAsync(cancellationToken: cancellationToken);
        }
        catch
        {
            // Log or retry later — but do NOT delete the new blob.
            throw new InvalidOperationException(
                $"Blob copied to '{newName}' but failed to delete original '{oldName}'.");
        }
    }
    #endregion
    #region Table Handling
    // Migrate data in the tables from the old partition key (orderId) to the new partition key (userKey)
    // The tables affected are Meal, PersonList and VenueList.
    private async Task<int> MigrateTableDataAsync<T>(string oldPartitionKey, string newPartitionKey, CancellationToken cancellationToken = default) where T : StorageClass, new()
    {
        //        internal readonly DataStore<VenueListStorage> venueListStorage = new(logger, licenseStore);

        DataStore<T> storage = new(logger, licenseStore);

        // var itemNames = await storage.SimpleEnumerateAsync(oldPartitionKey); // for testing

        int movedItemCount = await MovePartitionAsync(storage.TableClient, oldPartitionKey, newPartitionKey, cancellationToken);

        return movedItemCount;
    }
    /// <summary>
    /// Moves all entities from one PartitionKey to another.
    /// </summary>
    public static async Task<int> MovePartitionAsync(
        TableClient table,
        string oldPartitionKey,
        string newPartitionKey,
        CancellationToken cancellationToken = default)
    {
        int movedItemCount = 0;
        // Query all entities in the old partition
        await foreach (var entity in table.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{oldPartitionKey}'",
            cancellationToken: cancellationToken))
        {
            // Create new entity with new PartitionKey and same RowKey
            var newEntity = new TableEntity(newPartitionKey, entity.RowKey);

            // Copy all properties except keys
            foreach (var kvp in entity)
            {
                if (kvp.Key is not "PartitionKey" and not "RowKey")
                    newEntity[kvp.Key] = kvp.Value;
            }

            // Insert new entity
            await table.AddEntityAsync(newEntity, cancellationToken);

            // Delete old entity
            await table.DeleteEntityAsync(oldPartitionKey, entity.RowKey, cancellationToken: cancellationToken);

            // All done, increment the count
            movedItemCount++;
        }
        return movedItemCount;
    }

    #endregion
}