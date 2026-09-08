using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace DivisiBillWs;

internal partial class MigrationClass(ILogger logger)
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
        LogBlobMigrationStarted();
        await blobContainer.CreateIfNotExistsAsync();
        var blobsForOrderId = await GetBlobsWithPrefixAsync(oldKey, httpRequest.HttpContext.RequestAborted);
        int successCount = 0;
        int failureCount = 0;
        if (blobsForOrderId.Count > 0)
        {
            successCount = await RenameBlobsBatchAsync(oldKey, newKey, blobsForOrderId, httpRequest.HttpContext.RequestAborted);
            failureCount = blobsForOrderId.Count - successCount;
            if (failureCount > 0)
                LogBlobRenameFailure(failureCount, blobsForOrderId.Count);
        }
        LogBlobMigrationCompleted(blobsForOrderId.Count, successCount, failureCount);
        #endregion
        #region Migrate Tables
        int migratedMealCount = await MigrateTableDataAsync<MealStorage>(oldKey, newKey, httpRequest.HttpContext.RequestAborted);
        int migratedPersonListCount = await MigrateTableDataAsync<PersonListStorage>(oldKey, newKey, httpRequest.HttpContext.RequestAborted);
        int migratedVenueListCount = await MigrateTableDataAsync<VenueListStorage>(oldKey, newKey, httpRequest.HttpContext.RequestAborted);
        #endregion
        stopwatch.Stop();
        string resultMsg = (Utility.IsDebug ? $"OrderId: {oldKey}\nto UserKey: {newKey} in {stopwatch.ElapsedMilliseconds} ms,\n" : "")
            + $"Migrated Images:{successCount} of {successCount + failureCount}, Meals: {migratedMealCount}, PersonLists: {migratedPersonListCount}, VenueLists: {migratedVenueListCount} ";
        LogMigrationSummary(oldKey, newKey, stopwatch.ElapsedMilliseconds, successCount, successCount + failureCount, migratedMealCount, migratedPersonListCount, migratedVenueListCount);
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
                    if (await RenameBlobAsync(blob.Name, newBlobName, cancellationToken))
                        Interlocked.Increment(ref successCount);
                }
                catch (Exception ex)
                {
                    LogBlobRenameFaulted(blob.Name, ex.Message);
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

    /// <summary>
    /// Safely renames a blob by copying then deleting the original.
    /// Guarantees no data loss even if delete fails.
    /// Skips migration and returns false if destination blob already exists.
    /// </summary>
    private async Task<bool> RenameBlobAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        var target = blobContainer.GetBlobClient(newName);

        // Check if destination blob already exists
        if (await target.ExistsAsync(cancellationToken))
        {
            LogBlobAlreadyExists(newName);
            return false;
        }
        // Now we are sure the destination blob does not already exist get source blob client
        var source = blobContainer.GetBlobClient(oldName);

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
        return true;
    }
    #endregion
    #region Table Handling
    // Migrate data in the tables from the old partition key (oldKey) to the new partition key (newKey)
    // The tables affected are Meal, PersonList and VenueList.
    private async Task<int> MigrateTableDataAsync<T>(string oldKey, string newKey, CancellationToken cancellationToken = default) where T : StorageClass, new()
    {
        LogTableDataMigrationStarted(typeof(T).Name);

        DataStore<T> storage = new(logger, licenseStore);

        int movedItemCount = await MovePartitionAsync(storage.TableClient, oldKey, newKey, cancellationToken);

        LogTableDataMigrationCompleted(typeof(T).Name, movedItemCount);

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
    /// Processes a batch of table entities by adding them to the new partition and deleting from the old partition.
    /// We do it in that order so if the operation fails or is cancelled, worst case, we have duplicates.
    /// Skips insertion if the entity already exists in the destination partition.
    /// Only deletes entities from the old partition that were successfully inserted into the new partition.
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
        // Filter out entities that already exist in the destination
        var entitiesToInsert = new List<TableEntity>();
        var rowKeysToDelete = new List<string>();

        foreach (var (newEntity, rowKey) in batch)
        {
            try
            {
                await table.GetEntityAsync<TableEntity>(newEntity.PartitionKey, newEntity.RowKey, cancellationToken: cancellationToken);
                // Entity exists, skip it
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                // Entity doesn't exist, add it to insert list and mark for deletion
                entitiesToInsert.Add(newEntity);
                rowKeysToDelete.Add(rowKey);
            }
        }

        int insertedCount = 0;
        if (entitiesToInsert.Count > 0)
        {
            var insertTransaction = new List<TableTransactionAction>(entitiesToInsert.Count);
            foreach (var entity in entitiesToInsert)
                insertTransaction.Add(new TableTransactionAction(TableTransactionActionType.Add, entity));

            var insertResult = await table.SubmitTransactionAsync(insertTransaction, cancellationToken);

            var insertResponses = insertResult.Value;

            if (insertResponses.Where(r => r.IsError).FirstOrDefault() is { } response)
                throw new InvalidOperationException($"Failed to insert entity: {response.ReasonPhrase}");

            insertedCount = entitiesToInsert.Count;
        }

        // Step 2: Delete old entities (only those that were inserted)
        if (rowKeysToDelete.Count > 0)
        {
            var deleteTransaction = new List<TableTransactionAction>(rowKeysToDelete.Count);
            foreach (var rowKey in rowKeysToDelete)
            {
                var deleteEntity = new TableEntity(oldPartitionKey, rowKey) { ETag = ETag.All };
                deleteTransaction.Add(new TableTransactionAction(TableTransactionActionType.Delete, deleteEntity));
            }

            await table.SubmitTransactionAsync(deleteTransaction, cancellationToken);
        }

        return insertedCount;
    }
    #endregion

    [LoggerMessage(LogLevel.Warning, "Failed to rename {failureCount} out of {totalCount} blobs")]
    private partial void LogBlobRenameFailure(int failureCount, int totalCount);

    [LoggerMessage(LogLevel.Error, "Failed to rename blob {name}: {message}")]
    private partial void LogBlobRenameFaulted(string name, string message);

    [LoggerMessage(LogLevel.Debug, "Blob {name} already exists in destination, skipping migration")]
    private partial void LogBlobAlreadyExists(string name);

    [LoggerMessage(LogLevel.Information, "Migrated {oldKey} to {newKey} in {elapsedMilliseconds} ms, Migrated Images: {imageSuccesscount} of {imageCount}, Meals: {mealCount}, PersonLists: {personListCount}, VenueLists: {venueListCount}")]
    private partial void LogMigrationSummary(string oldKey, string newKey, long elapsedMilliseconds, int imageSuccessCount, int imageCount, int mealCount, int personListCount, int venueListCount);

    [LoggerMessage(LogLevel.Information, "Starting migration of blobs")]
    private partial void LogBlobMigrationStarted();

    [LoggerMessage(LogLevel.Information, "Completed migration of blobs total = {totalCount}, succeeded = {successCount} failed = {failureCount}")]
    private partial void LogBlobMigrationCompleted(int totalCount, int successCount, int failureCount);

    [LoggerMessage(LogLevel.Information, "Starting migration of table data for type {typeName}")]
    private partial void LogTableDataMigrationStarted(string typeName);

    [LoggerMessage(LogLevel.Information, "Completed migration of table data for type {typeName} with {movedItemCount} items moved")]
    private partial void LogTableDataMigrationCompleted(string typeName, int movedItemCount);
}
