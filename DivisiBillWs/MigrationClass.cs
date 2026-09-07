using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace DivisiBillWs;

internal class MigrationClass(ILogger logger)
{
    private readonly LicenseStore licenseStore = new(logger);
    private readonly BlobContainerClient blobContainer = new(Environment.GetEnvironmentVariable("AzureWebJobsStorage"), "images");

    /// <summary>
    /// Migrates blobs and table data from the old OrderId to the new UserKey.
    /// </summary>
    /// <param name="oldKey">The old OrderId</param>
    /// <param name="newKey">The new UserKey</param>
    /// <param name="httpRequest">The incoming HttpRequest object</param>
    /// <returns>An IActionResult indicating the result of the operation</returns>
    public async Task<IActionResult> RenameAsNeeded(string oldKey, string newKey, HttpRequest httpRequest)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        #region Migrate Blobs
        await blobContainer.CreateIfNotExistsAsync();
        var blobsForOrderId = await GetBlobsWithPrefixAsync(oldKey, httpRequest.HttpContext.RequestAborted);
        int successCount = 0;
        int failureCount = 0;
        if (blobsForOrderId.Count > 0)
        {
            successCount = await RenameBlobsBatchAsync(oldKey, newKey, blobsForOrderId, httpRequest.HttpContext.RequestAborted);
            failureCount = blobsForOrderId.Count - successCount;
            if (failureCount > 0)
            {
                logger.LogWarning($"Failed to rename {failureCount} out of {blobsForOrderId.Count} blobs for OrderId: {oldKey}");
            }
        }
        #endregion
        #region Migrate Tables
        int migratedMealCount = await MigrateTableDataAsync<MealStorage>(oldKey, newKey, httpRequest.HttpContext.RequestAborted);
        int migratedPersonListCount = await MigrateTableDataAsync<PersonListStorage>(oldKey, newKey, httpRequest.HttpContext.RequestAborted);
        int migratedVenueListCount = await MigrateTableDataAsync<VenueListStorage>(oldKey, newKey, httpRequest.HttpContext.RequestAborted);
        #endregion
        stopwatch.Stop();
        string resultMsg = (Utility.IsDebug ? $"OrderId: {oldKey}\nto UserKey: {newKey} in {stopwatch.ElapsedMilliseconds} ms,\n" : "")
            + $"Migrated Images:{successCount - failureCount}, Meals: {migratedMealCount}, PersonLists: {migratedPersonListCount}, VenueLists: {migratedVenueListCount} ";
        return new OkObjectResult(resultMsg);
    }
    #region Blob Handling
    private async Task<List<BlobItem>> GetBlobsWithPrefixAsync(string prefix, CancellationToken cancellationToken = default)
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
    private async Task RenameBlobAsync(string oldName, string newName, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Renames multiple blobs in parallel batches with error handling.
    /// Tracks successes and failures while respecting Azure throttling limits.
    /// </summary>
    private async Task<int> RenameBlobsBatchAsync(
        string oldKey,
        string newKey,
        List<BlobItem> blobs,
        CancellationToken cancellationToken = default)
    {
        const int maxConcurrentRenames = 5; // Azure throttling safe limit
        var semaphore = new SemaphoreSlim(maxConcurrentRenames);
        int successCount = 0;

        var renameTasks = blobs.Select(async blob =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                string newBlobName = $"{newKey}{blob.Name[oldKey.Length..]}";

                try
                {
                    await RenameBlobAsync(blob.Name, newBlobName, cancellationToken);
                    Interlocked.Increment(ref successCount);
                }
                catch (Exception ex)
                {
                    logger.LogError("Failed to rename blob {name}: {message}", blob.Name, ex.Message);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(renameTasks);
        semaphore.Dispose();

        return successCount;
    }
    #endregion
    #region Table Handling
    // Migrate data in the tables from the old partition key (oldKey) to the new partition key (newKey)
    // The tables affected are Meal, PersonList and VenueList.
    private async Task<int> MigrateTableDataAsync<T>(string oldKey, string newKey, CancellationToken cancellationToken = default) where T : StorageClass, new()
    {
        //        internal readonly DataStore<VenueListStorage> venueListStorage = new(logger, licenseStore);

        DataStore<T> storage = new(logger, licenseStore);

        // var itemNames = await storage.SimpleEnumerateAsync(oldKey); // for testing

        int movedItemCount = await MovePartitionAsync(storage.TableClient, oldKey, newKey, cancellationToken);

        return movedItemCount;
    }
    /// <summary>
    /// Moves all entities from one PartitionKey to another.
    /// Uses batch operations to handle large numbers of entries efficiently.
    /// </summary>
    private static async Task<int> MovePartitionAsync(
        TableClient table,
        string oldKey,
        string newKey,
        CancellationToken cancellationToken = default)
    {
        int movedItemCount = 0;
        const int itemCountEstimate = 5000; // a reasonable guess at the maximum number of items
        const int batchSize = 100; // Azure Tables limit per transaction

        var batch = new List<(TableEntity newEntity, string rowKey)>(itemCountEstimate / batchSize);

        // Query all entities in the old partition
        await foreach (var entity in table.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{oldKey}'",
            cancellationToken: cancellationToken))
        {
            // Create new entity with new PartitionKey and same RowKey
            var newEntity = new TableEntity(newKey, entity.RowKey);

            // Copy all properties except keys
            foreach (var kvp in entity.Where(kvp => kvp.Key is not "PartitionKey" and not "RowKey"))
                newEntity[kvp.Key] = kvp.Value;

            batch.Add((newEntity, entity.RowKey));

            // Process batch when it reaches the size limit
            if (batch.Count >= batchSize)
            {
                movedItemCount += await ProcessBatchAsync(table, oldKey, batch, cancellationToken);
                batch.Clear();
            }
        }

        // Process remaining entities
        if (batch.Count > 0)
            movedItemCount += await ProcessBatchAsync(table, oldKey, batch, cancellationToken);

        return movedItemCount;
    }

    /// <summary>
    /// Processes a batch of entities by adding them to the new partition and deleting from the old partition.
    /// We do it in that order so if the operation fails or is cancelled, worst case, we have duplicates
    /// </summary>
    private static async Task<int> ProcessBatchAsync(
        TableClient table,
        string oldPartitionKey,
        List<(TableEntity newEntity, string rowKey)> batch,
        CancellationToken cancellationToken = default)
    {
        // Azure Tables requires all entities in a batch to have the same PartitionKey
        // Process inserts and deletes sequentially, each with their respective partition keys

        // Step 1: Insert new entities (all have newKey from newEntity)
        var insertTransaction = new List<TableTransactionAction>(batch.Count);
        foreach (var (newEntity, _) in batch)
            insertTransaction.Add(new TableTransactionAction(TableTransactionActionType.Add, newEntity));

        var insertResult = await table.SubmitTransactionAsync(insertTransaction, cancellationToken);

        var insertResponses = insertResult.Value;

        if (insertResponses.Where(r => r.IsError).FirstOrDefault() is { } response)
            throw new InvalidOperationException($"Failed to insert entity: {response.ReasonPhrase}");

        // Step 2: Delete old entities (all have oldKey)
        var deleteTransaction = new List<TableTransactionAction>(batch.Count);
        foreach (var (_, rowKey) in batch)
        {
            var deleteEntity = new TableEntity(oldPartitionKey, rowKey) { ETag = ETag.All };
            deleteTransaction.Add(new TableTransactionAction(TableTransactionActionType.Delete, deleteEntity));
        }

        await table.SubmitTransactionAsync(deleteTransaction, cancellationToken);

        return batch.Count;
    }
    #endregion

}
