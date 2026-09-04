using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace DivisiBillWs;

/// <summary>
/// Generic class used to access lists of Meals, lists of people lists, or lists of Venue lists 
/// </summary>
/// <typeparam name="T">The storage type to use</typeparam>
internal class DataStore<T> where T : StorageClass, new()
{
    private readonly T storageClass = new();
    private const string TableNamePrefix =
#if DEBUG
        "DivisiBillDebug";
#else
        "DivisiBill";
#endif
    private class EnumeratedDataItem(string name, long dataLength, string data, bool isEncrypted, string? summary = null, bool hasRemoteImage = false)
    {
        public string Name { get; set; } = name;
        public string Data { get; set; } = data;
        public long DataLength { get; set; } = dataLength;
        public string? Summary { get; set; } = summary;
        public bool HasRemoteImage { get; set; } = hasRemoteImage; // This will be set to true if the image exists in blob storage
        public bool IsEncrypted { get; set; } = isEncrypted;
    }
    private class DataFormat : ITableEntity
    {
        // Item information
        public string Data { get; set; } = "";
        public long DataLength { get; set; } = 0;
        public string Summary { get; set; } = "";
        public bool IsEncrypted { get; set; } = false;
        public bool IsStoredInBlob { get; set; } = false; // True if data is stored in blob storage instead of table

        // Required for ITableEntity
        public string RowKey { get; set; } = ""; // User must provide a value
        public string PartitionKey { get; set; } = ""; // User must provide a value
        public ETag ETag { get; set; } // Value optional
        public DateTimeOffset? Timestamp { get; set; } = null; // Set by system whenever item is changed
    }

    private readonly string TableName;
    internal DataStore(ILogger loggerParam, LicenseStore? licenseStoreParam)
    {
        TableName = TableNamePrefix + storageClass.TableName;
        tableClient = tableServiceClient.GetTableClient(tableName: TableName);
        tableClient.CreateIfNotExists();
        logger = loggerParam;
        licenseStore = licenseStoreParam;
    }

    private readonly LicenseStore? licenseStore;
    private readonly ILogger logger;
    private static readonly string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")!;
    private static readonly TableServiceClient tableServiceClient = new(connectionString);

    // New instance of TableClient class referencing the server-side table
    private readonly TableClient tableClient;

    #region Interface Properties and Methods

    internal TableClient TableClient => tableClient;
    public async Task<IActionResult> PutAsync(HttpRequest httpRequest, string userKey, string dataName)
    {
        const string logMessageTemplate = "In DataStore.PutAsync, upsert data to {TableName}[{UserKey}, {DataName}({InvertedDataName})";
        logger.LogInformation(logMessageTemplate, tableClient.Name, userKey, dataName, dataName.Invert());

        if (!dataName.IsValidName())
            return new BadRequestResult();
        // Get the data stream
        var formCollection = await httpRequest.ReadFormAsync();
        // To be a legal message either all fields must be encrypted, or none. An encrypted field is stored as a file form element and a plaintext one as a string form element
        if (formCollection is null)
            return new BadRequestObjectResult("A form collection is required");
        if ((formCollection.Count > 0) == (formCollection.Files.Count > 0))
        {
            const string fieldsAndFilesExclusiveMessage = "In DataStore.PutAsync, exactly one of fields and forms may be nonzero";
            logger.LogError(fieldsAndFilesExclusiveMessage);
            return new BadRequestObjectResult(fieldsAndFilesExclusiveMessage);
        }
        // If there are any files, then all fields must be files
        bool isEncrypted = formCollection.Files.Count > 0;
        // Get the data field, which may be either a string or an encrypted file
        string? dataValue = await GetFormFieldValueAsync("data");
        if (dataValue is null)
            return new BadRequestObjectResult("Missing 'data' field");

        const long BlobStorageThreshold = 24000; // 24 KB - conservative threshold, allows doubling for Unicode and still stays within 64 KB table storage limit
        bool storedInBlob = dataValue.Length > BlobStorageThreshold;

        // If data is large, store in blob storage
        if (storedInBlob)
        {
            try
            {
                string blobContainerName = storageClass.TableName.ToLower();
                var dataBlobContainer = new BlobContainerClient(connectionString, blobContainerName);
                await dataBlobContainer.CreateIfNotExistsAsync();

                var dataBlob = dataBlobContainer.GetBlobClient($"{userKey}/{dataName}.dat");

                // Convert data to bytes for blob storage
                byte[] dataBytes;
                if (isEncrypted)
                {
                    // Data is base64-encoded, decode it to binary
                    dataBytes = Convert.FromBase64String(dataValue);
                }
                else
                {
                    // Data is plain text, encode to UTF-8 bytes
                    dataBytes = System.Text.Encoding.UTF8.GetBytes(dataValue);
                }

                var uploadResult = await dataBlob.UploadAsync(new MemoryStream(dataBytes), overwrite: true);
                if (uploadResult.GetRawResponse().IsError)
                    return Utility.CreateFailedResult("Failed to upload large data to blob storage.");

                logger.LogInformation("In DataStore.PutAsync, stored large data ({DataLength} bytes) in blob storage", dataBytes.Length);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "In DataStore.PutAsync, failed to store large data in blob storage");
                return Utility.CreateFailedResult("Failed to store large data in blob storage: " + ex.Message);
            }
        }

        // Create a new entry
        DataFormat data = new()
        {
            PartitionKey = userKey,
            RowKey = dataName.Invert(),
            Data = storedInBlob ? "" : dataValue, // Keep empty if stored in blob
            IsEncrypted = isEncrypted,
            DataLength = dataValue == null ? 0 : dataValue.Length,
            IsStoredInBlob = storedInBlob
        };
        // Add the optional summary field (only used with meal storage)
        if (storageClass.UseSummaryField)
        {
            string? summaryValue = await GetFormFieldValueAsync("summary");
            if (summaryValue is null)
                return new BadRequestObjectResult("Missing 'summary' field");
            data.Summary = summaryValue;
        }
        var addEntityResponse = await tableClient.UpsertEntityAsync(data);
        return addEntityResponse.IsError ? new BadRequestResult() : new OkResult();

        // Local function to return a string representing a named form element which may be either from an encrypted 'file' or a string
        async Task<string?> GetFormFieldValueAsync(string? formName)
        {
            ArgumentNullException.ThrowIfNullOrWhiteSpace(formName);
            if (isEncrypted)
            {
                IFormFile? formFile = formCollection.Files[formName];
                if (formFile is null || formFile.Length == 0)
                    return null;
                using var stream = new MemoryStream();
                await formFile.CopyToAsync(stream);
                byte[] encryptedBlob = stream.ToArray();
                return Convert.ToBase64String(encryptedBlob);
            }
            else // Just return plain text, this is compatible with pre-encryption clients
            {
                var summary = formCollection[formName];
                if (summary.Count != 1)
                    return null;
                return summary[0];
            }
        }
    }
    public async Task<IActionResult> GetAsync(string userKey, string dataName)
    {
        const string logMessageTemplate = "In DataStore.GetAsync, retrieve data from {TableName}[{UserKey}, {DataName}({InvertedDataName})]";
        logger.LogInformation(logMessageTemplate, tableClient.Name, userKey, dataName, dataName.Invert());
        if (!dataName.IsValidName())
        {
            const string invalidNameLogMessage = "In DataStore.GetAsync, invalid name '{DataName}'";
            logger.LogError(invalidNameLogMessage, dataName);
            return new BadRequestResult();
        }
        logger.LogInformation("In DataStore.GetAsync, {DataName} was a legal data name", dataName);
        try
        {
            // Get data for named entry in specific Order
            var data = await tableClient.GetEntityIfExistsAsync<DataFormat>(userKey, dataName.Invert());
            if (data.Value is not null)
            {
                logger.LogInformation("In DataStore.GetAsync, got data, length = {DataLength}, encrypted = {IsEncrypted}, storedInBlob = {IsStoredInBlob}",
                    data.Value.DataLength, data.Value.IsEncrypted, data.Value.IsStoredInBlob);

                string dataValue = data.Value.Data;

                // If data is stored in blob, retrieve it
                if (data.Value.IsStoredInBlob)
                {
                    try
                    {
                        string blobContainerName = storageClass.TableName.ToLower();
                        var dataBlobContainer = new BlobContainerClient(connectionString, blobContainerName);
                        var dataBlob = dataBlobContainer.GetBlobClient($"{userKey}/{dataName}.dat");

                        var download = await dataBlob.DownloadAsync();
                        using var stream = download.Value.Content;
                        using var memoryStream = new MemoryStream();
                        await stream.CopyToAsync(memoryStream);
                        byte[] blobBytes = memoryStream.ToArray();

                        // Convert blob bytes back to the original format
                        if (data.Value.IsEncrypted)
                        {
                            // Blob contains binary encrypted data, convert back to base64
                            dataValue = Convert.ToBase64String(blobBytes);
                        }
                        else
                        {
                            // Blob contains UTF-8 encoded plain text, decode back to string
                            dataValue = System.Text.Encoding.UTF8.GetString(blobBytes);
                        }

                        logger.LogInformation("In DataStore.GetAsync, retrieved large data ({DataLength} bytes) from blob storage", blobBytes.Length);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "In DataStore.GetAsync, failed to retrieve data from blob storage");
                        return new ObjectResult("Failed to retrieve large data from blob storage: " + ex.Message)
                        {
                            StatusCode = StatusCodes.Status500InternalServerError
                        };
                    }
                }

                return data.Value.IsEncrypted
                    ? new FileContentResult(Convert.FromBase64String(dataValue), "application/octet-stream")
                    : new OkObjectResult(dataValue);
            }
            else
            {
                const string noDataFoundLogMessage = "In DataStore.GetAsync, no data found";
                logger.LogError(noDataFoundLogMessage);
                return new BadRequestResult();
            }
        }
        catch (Exception)
        {
            const string faultedLogMessage = "In DataStore.GetAsync, faulted";
            logger.LogError(faultedLogMessage);
            return new ObjectResult(faultedLogMessage) { StatusCode = StatusCodes.Status500InternalServerError };
        }
    }
    public async Task<IActionResult> DeleteAsync(string userKey, string dataName)
    {
        const string logMessageTemplate = "In DataStore.Delete, delete data at {TableName}[{UserKey}, {DataName}({InvertedDataName})]";
        logger.LogInformation(logMessageTemplate, tableClient.Name, userKey, dataName, dataName.Invert());
        if (!dataName.IsValidName())
            return new BadRequestResult();

        // First check if data is stored in blob before deleting from table
        var dataEntity = await tableClient.GetEntityIfExistsAsync<DataFormat>(userKey, dataName.Invert());
        bool wasStoredInBlob = dataEntity.Value?.IsStoredInBlob ?? false;

        // Delete Entry
        var deleteResult = await tableClient.DeleteEntityAsync(userKey, dataName.Invert());
        if (deleteResult.IsError)
            return new NotFoundResult();
        else
        {
            string blobContainerName = storageClass.TableName.ToLower();
            var dataBlobContainer = new BlobContainerClient(connectionString, blobContainerName);
            var imagesBlobContainer = new BlobContainerClient(connectionString, "images");

            try
            {
                // Delete data blob if it was stored there
                if (wasStoredInBlob)
                {
                    var deleteDataBlob = dataBlobContainer.GetBlobClient($"{userKey}/{dataName}.dat");
                    await deleteDataBlob.DeleteIfExistsAsync();
                    logger.LogInformation("In DataStore.Delete, deleted blob data for {DataName}", dataName);
                }

                // Now delete any accompanying image (both encrypted and unencrypted versions)
                var deleteBlob = imagesBlobContainer.GetBlobClient(userKey + "/" + dataName + ".jpg");
                var deleteEncryptedBlob = imagesBlobContainer.GetBlobClient(userKey + "/" + dataName + ".jpg.enc");
                try
                {
                    // At most one of these files should be present, but it's cheaper to delete them both than to query then delete
                    await deleteBlob.DeleteIfExistsAsync();
                    await deleteEncryptedBlob.DeleteIfExistsAsync();
                }
                catch (RequestFailedException)
                {
                    return new StatusCodeResult(StatusCodes.Status500InternalServerError);
                    throw;
                }
                return new OkResult();
            }
            catch (RequestFailedException ex)
            {
                logger.LogError(ex, "In DataStore.Delete, failed to delete blob data");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }
    }

    /// <summary>
    /// Asynchronously enumerates the names of data entries for a specific user in the data store.
    /// </summary>
    /// <param name="userKey">The key of the user whose data entries are to be enumerated.</param>
    /// <returns>A list of entry names for the specified user key.</returns>
    public async Task<List<string>> SimpleEnumerateAsync(string userKey)
    {
        List<string> responseList = [];
        string query = $"PartitionKey eq '{userKey}'";
        // Determine which fieldNames to return
        List<string> fieldNames = ["RowKey"]; // Note that "Data" is not included
        // Find entries
        var returnedPages = tableClient.QueryAsync<DataFormat>(query, null, fieldNames);
        if (returnedPages == null)
            return [];
        else
        {
            await foreach (var item in returnedPages)
            {
                responseList.Add(item.RowKey.Invert());
            }
            return responseList;
        }
    }
    public async Task<IActionResult> EnumerateAsync(HttpRequest httpRequest)
    {
        var parsedQueryString = httpRequest.Query;
        string? before = parsedQueryString["before"];
        string? topString = parsedQueryString["top"];

        string? userKey = httpRequest.HttpContext.Items["userKey"] as string;

        // Validate 'before'
        if (before != null && !before.IsValidName())
        {
            logger.LogError($"The 'before' specification is unacceptable, returning error");
            return new BadRequestResult();
        }

        const int MaxItems = 1000;
        if (string.IsNullOrWhiteSpace(topString) || !int.TryParse(topString, out int top) || top > MaxItems || top < 1)
            return new BadRequestResult();

        const string logMessageTemplate = "In DataStore.Enumerate, enumerate data in {TableName}, before = '{Before}'";
        logger.LogInformation(logMessageTemplate, tableClient.Name, before);
        string query = $"PartitionKey eq '{userKey}'";
        if (!string.IsNullOrWhiteSpace(before))
            query += " and RowKey gt '" + before.Invert() + "'";
        List<string> fieldNames = ["RowKey", "DataLength", "IsEncrypted"]; // Note that "Data" is not included
        if (storageClass.UseSummaryField) fieldNames.Add("Summary");
        // Find entries
        var returnedPages = tableClient.QueryAsync<DataFormat>(query, null, fieldNames);
        if (returnedPages == null)
            return new BadRequestResult();
        else
        {
            // We have a list, we may need to see if they have corresponding images
            string? connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            BlobContainerClient? imagesBlobContainer = null;

            if (storageClass.CheckImage)
            {
                imagesBlobContainer = new BlobContainerClient(connectionString, "images");
                await imagesBlobContainer.CreateIfNotExistsAsync();
            }

            int count = 0;
            var responseList = new List<EnumeratedDataItem>();

            try
            {
                await foreach (var item in returnedPages)
                {
                    string imageBlobName = userKey + "/" + item.RowKey.Invert() + (item.IsEncrypted ? ".jpg.enc" : ".jpg");
                    BlobClient? blobClient = imagesBlobContainer?.GetBlobClient(imageBlobName);
                    responseList.Add(new EnumeratedDataItem(item.RowKey.Invert(), item.DataLength, item.Data, item.IsEncrypted,
                        storageClass.UseSummaryField ? item.Summary : null,
                        blobClient is not null ? await blobClient.ExistsAsync() : false));
                    if (++count >= top) break;
                }
                return new JsonResult(responseList, new JsonSerializerOptions()
                {
                    PropertyNamingPolicy = null,
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
            }
            catch (Exception ex)
            {
                // return internal server error
                return new ObjectResult("Fault in StorageClass.EnumerateAsync: " + ex.Message)
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }
    }
    public async Task<IActionResult> DeleteAllAsync(HttpRequest httpRequest)
    {
        string? userKey = httpRequest.HttpContext.Items["userKey"] as string;

        const string logMessageTemplate = "In DataStore.DeleteAllAsync, delete data in {TableName}";
        logger.LogInformation(logMessageTemplate, tableClient.Name);

        var deleteTasks = new List<Task>();

        int deleteCount = 0;
        int failCount = 0;

        await foreach (var entity in tableClient.QueryAsync<TableEntity>(e => e.PartitionKey == userKey))
        {
            deleteTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                    Interlocked.Increment(ref deleteCount);
                }
                catch
                {
                    Interlocked.Increment(ref failCount);
                }
            }));
        }

        await Task.WhenAll(deleteTasks);
        logger.LogInformation("In DataStore.DeleteAllAsync, deleted {successes} in {TableName}, failed to delete {failures}", deleteCount, tableClient.Name, failCount);
        return failCount > 0
            ? Utility.CreateFailedResult($"Failed to delete {failCount} items, deleted {deleteCount}.")
            : new OkObjectResult($"Deleted {deleteCount} items.");
    }
    #endregion
    private static readonly (TimeSpan Age, TimeSpan Period)[] Schedule =
    [
        (TimeSpan.FromHours(4), TimeSpan.FromMinutes(10)),
        (TimeSpan.FromDays(2), TimeSpan.FromHours(1)),
        (TimeSpan.FromDays(7*2), TimeSpan.FromDays(1)),
        (TimeSpan.FromDays(7*8), TimeSpan.FromDays(7)),
        (TimeSpan.FromDays(7*104), TimeSpan.FromDays(7*4)),
        (TimeSpan.MaxValue, TimeSpan.MaxValue) // Stopper value
    ];
    /// <summary>
    /// Asynchronously removes outdated user data entries for all users from a data store based on age categories and
    /// retention periods.
    /// </summary>
    /// <remarks>This method iterates through all user data entries and applies retention policies (hard coded in <see cref="Schedule"/> to
    /// determine which entries should be deleted. The age-related policies are relative to the youngest item
    /// rather than being absolute age so that lists that are no longer actively being updated do not simply "age out".
    /// Entries that do not meet the criteria for retention are removed. The
    /// operation is logged for auditing purposes. This method is intended for internal use and is not
    /// thread-safe.</remarks>
    /// <returns>A task that represents the asynchronous cleanup operation.</returns>
    internal async Task CleanupAllUsersAsync()
    {
        const string logMessageTemplate = "In DataStore.CleanupAllUsers, cleanup items in {TableName}";
        logger.LogInformation(logMessageTemplate, tableClient.Name);

        string? filter = null;
        var entities = tableClient.QueryAsync<DataFormat>(
            filter: filter,
            maxPerPage: null,
            select: ["PartitionKey", "RowKey"]);

        string partKey = string.Empty;
        DateTime youngest = DateTime.MaxValue;
        DateTime previous = youngest;
        var currentSchedule = Schedule[0];
        await foreach (var entity in entities)
        {
            if (!partKey.Equals(entity.PartitionKey, StringComparison.Ordinal))
            {
                // A new user, reset everything
                partKey = entity.PartitionKey; // Note the change of user
                youngest = entity.RowKey.ToDateTime(); ;
                previous = youngest;
                currentSchedule = Schedule[0];
                logger.LogInformation("In DataStore.CleanupAllUsers for {TableName}, switched to {PartKey}", tableClient.Name, partKey);
            }
            else
            {
                // Next row for the same user
                DateTime dateTime = entity.RowKey.ToDateTime();
                TimeSpan age = youngest - dateTime;
                if (age == TimeSpan.Zero)
                    continue; // Skip the first item
                if (currentSchedule.Age < age)
                {
                    // We need to move to an older age category 
                    currentSchedule = Schedule.First(s => s.Age > age);
                    previous = dateTime; // Remember the last one we kept
                    continue;
                }
                // We are within the current age category
                TimeSpan period = previous - dateTime;
                if (period < currentSchedule.Period)
                {
                    // This item is too close in time to the previous one, delete it.
                    await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                    logger.LogInformation("In DataStore.CleanupAllUsers for {TableName}, deleted item for {Time}", tableClient.Name, dateTime);
                }
                else
                {
                    // Keep this item and remember we kept it
                    previous = dateTime;
                }
            }
        }
    }
}