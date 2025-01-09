using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    private class EnumeratedDataItem
    {
        public EnumeratedDataItem(string name, long dataLength, string data, string? summary = null)
        {
            Name = name;
            DataLength = dataLength;
            Data = data;
            Summary = summary;
        }
        public string Name { get; set; } = default!;
        public string Data { get; set; } = default!;
        public long DataLength { get; set; } = default!;
        public string? Summary { get; set; } = default!;
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
        logger.LogInformation($"In DataStore.PutAsync, upsert data to {tableClient.Name}[{userKey}, {dataName}({dataName.Invert()})]");

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
        return addEntityResponse.IsError
            ? new BadRequestResult()
            : httpRequest.OkResponseWithToken(licenseStore.GetTokenIfNew(userKey));
    }
    public async Task<IActionResult> GetAsync(HttpRequest httpRequest, string userKey, string dataName)
    {
        logger.LogInformation($"In DataStore.GetAsync, retrieve data from {tableClient.Name}[{userKey}, {dataName}({dataName.Invert()})]");
        if (!dataName.IsValidName())
        {
            logger.LogError($"In DataStore.GetAsync, invalid name '{dataName}'");
            return new BadRequestResult();
        }
        logger.LogInformation($"In DataStore.GetAsync, '{dataName}' was a legal data name");
        // Get data for named entry in specific Order
        var data = await tableClient.GetEntityIfExistsAsync<DataFormat>(userKey, dataName.Invert());
        if (data.HasValue)
        {
            logger.LogInformation($"In DataStore.GetAsync, got data, length = {data!.Value!.DataLength}");
            string? token = licenseStore.GetTokenIfNew(userKey);
            logger.LogInformation($"In DataStore.GetAsync, called licenseStore.GetTokenIfNew, returned {(token is null ? "null" : "value")}");
            return httpRequest.OkResponseWithToken(token, data!.Value!.Data);
        }
        else
        {
            logger.LogError($"In DataStore.GetAsync, no data found");
            return new BadRequestResult();
        }
    }
    public async Task<IActionResult> DeleteAsync(HttpRequest httpRequest, string userKey, string dataName)
    {
        logger.LogInformation($"In DataStore.Delete, delete data at {tableClient.Name}[{userKey}, {dataName}({dataName.Invert()})]");
        if (!dataName.IsValidName())
            return new BadRequestResult();
        // Delete Entry
        var deleteResult = await tableClient.DeleteEntityAsync(userKey, dataName.Invert());
        return deleteResult.IsError
            ? new NotFoundResult()
            : httpRequest.OkResponseWithToken(licenseStore.GetTokenIfNew(userKey));
    }
    public async Task<IActionResult> EnumerateAsync(HttpRequest httpRequest, string userKey)
    {
        var parsedQueryString = httpRequest.Query;
        string? before = parsedQueryString["before"];
        string? topString = parsedQueryString["top"];

        // Validate 'before'
        if (before != null && !before.IsValidName())
        {
            logger.LogInformation($"The 'before' specification is unacceptable, returning error");
            return new BadRequestResult();
        }

        const int MaxItems = 1000;
        if (string.IsNullOrWhiteSpace(topString) || !int.TryParse(topString, out int top) || top > MaxItems || top < 1)
            return new BadRequestResult();

        logger.LogInformation($"In DataStore.Enumerate, enumerate data in {tableClient.Name}, before = '{before}'");
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
            int count = 0;

            var responseList = new List<EnumeratedDataItem>();

            await foreach (var item in returnedPages)
            {
                responseList.Add(new EnumeratedDataItem(item.RowKey.Invert(), item.DataLength, item.Data,
                    storageClass.UseSummaryField ? item.Summary : null));
                if (++count >= top) break;
            }
            return httpRequest.OkResponseWithToken(licenseStore.GetTokenIfNew(userKey), JsonSerializer.Serialize(responseList,
                new JsonSerializerOptions() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull })
            );
        }
    }
    #endregion
}