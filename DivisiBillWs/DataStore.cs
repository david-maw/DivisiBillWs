using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
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
    private class EnumeratedDataItem(string name, long dataLength, string data, string? summary = null, bool hasRemoteImage = false)
    {
        public string Name { get; set; } = name;
        public string Data { get; set; } = data;
        public long DataLength { get; set; } = dataLength;
        public string? Summary { get; set; } = summary;
        public bool HasRemoteImage { get; set; } = hasRemoteImage; // This will be set to true if the image exists in blob storage
    }
    private class DataFormat : ITableEntity
    {
        // Item information
        public string Data { get; set; } = default!;
        public long DataLength { get; set; } = default!;
        public string Summary { get; set; } = default!;

        // Required for ITableEntity
        public string RowKey { get; set; } = default!; // User must provide a value
        public string PartitionKey { get; set; } = default!; // User must provide a value
        public ETag ETag { get; set; } = default!; // Value optional
        public DateTimeOffset? Timestamp { get; set; } = default!; // Set by system whenever item is changed
    }

    private readonly string TableName;
    internal DataStore(ILogger loggerParam, LicenseStore licenseStoreParam)
    {
        TableName = TableNamePrefix + storageClass.TableName;
        tableClient = tableServiceClient.GetTableClient(tableName: TableName);
        tableClient.CreateIfNotExists();
        logger = loggerParam;
        licenseStore = licenseStoreParam;
    }

    private readonly LicenseStore licenseStore;
    private readonly ILogger logger;
    private static readonly string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")!;
    private static readonly TableServiceClient tableServiceClient = new(connectionString);

    // New instance of TableClient class referencing the server-side table
    private readonly TableClient tableClient;

    #region Interface Methods
    public async Task<IActionResult> PutAsync(HttpRequest httpRequest, string userKey, string dataName)
    {
        const string logMessageTemplate = "In DataStore.PutAsync, upsert data to {TableName}[{UserKey}, {DataName}({InvertedDataName})]";
        logger.LogInformation(logMessageTemplate, tableClient.Name, userKey, dataName, dataName.Invert());

        if (!dataName.IsValidName())
            return new BadRequestResult();
        // Get the data stream
        var forms = await httpRequest.ReadFormAsync();
        if (!forms.TryGetValue("data", out StringValues stringValues) || stringValues.Count != 1)
            return new BadRequestResult();

        string mainData = stringValues[0]!;

        // Create a new entry
        DataFormat data = new()
        {
            PartitionKey = userKey,
            RowKey = dataName.Invert(),
            Data = mainData,
            DataLength = mainData == null ? 0 : mainData.Length
        };
        if (storageClass.UseSummaryField)
        {
            if (!forms.TryGetValue("summary", out stringValues) || stringValues.Count != 1)
                return new BadRequestResult();
            string summaryData = stringValues[0]!;
            data.Summary = summaryData;
        }
        var addEntityResponse = await tableClient.UpsertEntityAsync(data);
        return addEntityResponse.IsError ? new BadRequestResult() : new OkResult();
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
        // Get data for named entry in specific Order
        var data = await tableClient.GetEntityIfExistsAsync<DataFormat>(userKey, dataName.Invert());
        if (data.HasValue)
        {
            logger.LogInformation("In DataStore.GetAsync, got data, length = {DataLength}", data!.Value!.DataLength);
            return new OkObjectResult(data!.Value!.Data);
        }
        else
        {
            const string noDataFoundLogMessage = "In DataStore.GetAsync, no data found";
            logger.LogError(noDataFoundLogMessage);
            return new BadRequestResult();
        }
    }
    public async Task<IActionResult> DeleteAsync(string userKey, string dataName)
    {
        const string logMessageTemplate = "In DataStore.Delete, delete data at {TableName}[{UserKey}, {DataName}({InvertedDataName})]";
        logger.LogInformation(logMessageTemplate, tableClient.Name, userKey, dataName, dataName.Invert());
        if (!dataName.IsValidName())
            return new BadRequestResult();
        // Delete Entry
        var deleteResult = await tableClient.DeleteEntityAsync(userKey, dataName.Invert());
        if (deleteResult.IsError)
            return new NotFoundResult();
        else
        {
            // Now delete any accompanying image
            var imagesBlobContainer = new BlobContainerClient(connectionString, "images");
            var deleteBlob = imagesBlobContainer.GetBlobClient(userKey + "/" + dataName + ".jpg");
            try
            {
                await deleteBlob.DeleteIfExistsAsync();
            }
            catch (RequestFailedException)
            {
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
                throw;
            }
            return new OkResult();
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
            logger.LogInformation($"The 'before' specification is unacceptable, returning error");
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
        // Determine which fieldNames to return
        List<string> fieldNames = ["RowKey", "DataLength"];
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

            await foreach (var item in returnedPages)
            {
                BlobClient? blobClient = imagesBlobContainer?.GetBlobClient(userKey + "/" + item.RowKey.Invert() + ".jpg");
                responseList.Add(new EnumeratedDataItem(item.RowKey.Invert(), item.DataLength, item.Data,
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
    }
    #endregion
}