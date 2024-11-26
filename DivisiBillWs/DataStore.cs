using Azure;
using Azure.Data.Tables;
using HttpMultipartParser;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DivisiBillWs
{
    /// <summary>
    /// Generic class used to access lists of Meals, lists of people lists, or lists of Venue lists 
    /// </summary>
    /// <typeparam name="T">The storage type to use</typeparam>
    internal class DataStore<T> where T : StorageClass, new()
    {
        readonly T storageClass = new();

        const string TableNamePrefix =
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


        readonly string TableName;
        internal DataStore(ILogger loggerParam, LicenseStore licenseStoreParam)
        {
            TableName = TableNamePrefix + storageClass.TableName;
            tableClient = tableServiceClient.GetTableClient(tableName: TableName);
            tableClient.CreateIfNotExists();
            logger = loggerParam;
            licenseStore = licenseStoreParam;
        }

        LicenseStore licenseStore;

        ILogger logger;

        static readonly string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")!;

        static readonly TableServiceClient tableServiceClient = new TableServiceClient(connectionString);

        // New instance of TableClient class referencing the server-side table
        readonly TableClient tableClient;

        #region Interface Methods
        public async Task<HttpResponseData> PutAsync(HttpRequestData req, string userKey, string dataName)
        {
            logger.LogInformation($"In DataStore.PutAsync, upsert data to {tableClient.Name}[{userKey}, {dataName}({dataName.Invert()})]");

            if (!dataName.IsValidName())
                return await req.MakeResponseAsync(HttpStatusCode.NotImplemented);
            // Get the data stream
            MultipartFormDataParser parsedFormBody = await MultipartFormDataParser.ParseAsync(req.Body);
            string mainData = parsedFormBody.GetParameterValue("data");
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
                string summaryData = parsedFormBody.GetParameterValue("summary");
                data.Summary = summaryData;
            }
            var addEntityResponse = await tableClient.UpsertEntityAsync(data);
            return addEntityResponse.IsError
                ? await req.MakeResponseAsync(HttpStatusCode.BadRequest)
                : await req.OkResponseAsync(licenseStore.GetTokenIfNew(userKey));
        }
        public async Task<HttpResponseData> GetAsync(HttpRequestData req, string userKey, string dataName)
        {
            logger.LogInformation($"In DataStore.Get, retrieve data from {tableClient.Name}[{userKey}, {dataName}({dataName.Invert()})]");
            if (!dataName.IsValidName())
                return await req.MakeResponseAsync(HttpStatusCode.NotImplemented);
            // Get data for named entry in specific Order
            var data = await tableClient.GetEntityIfExistsAsync<DataFormat>(userKey, dataName.Invert());
            return data.HasValue
                ? await req.OkResponseAsync(licenseStore.GetTokenIfNew(userKey), data!.Value!.Data)
                : await req.MakeResponseAsync(HttpStatusCode.NotFound);
        }
        public async Task<HttpResponseData> DeleteAsync(HttpRequestData req, string userKey, string dataName)
        {
            logger.LogInformation($"In DataStore.Delete, delete data at {tableClient.Name}[{userKey}, {dataName}({dataName.Invert()})]");
            if (!dataName.IsValidName())
                return await req.MakeResponseAsync(HttpStatusCode.NotImplemented);
            // Delete Entry
            var deleteResult = await tableClient.DeleteEntityAsync(userKey, dataName.Invert());
            return deleteResult.IsError
                ? await req.MakeResponseAsync(HttpStatusCode.NotFound)
                : await req.OkResponseAsync(licenseStore.GetTokenIfNew(userKey));
        }
        public async Task<HttpResponseData> EnumerateAsync(HttpRequestData req, string userKey)
        {
            var parsedQueryString = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            string? before = parsedQueryString["before"];
            string? topString = parsedQueryString["top"];

            // Validate 'before'
            if (before != null && !before.IsValidName())
            {
                logger.LogInformation($"The 'before' specification is unacceptable, returning error");
                return await req.MakeResponseAsync(HttpStatusCode.BadRequest);
            }

            const int MaxItems = 1000;
            if (string.IsNullOrWhiteSpace(topString) || !int.TryParse(topString, out int top) || top > MaxItems || top < 1)
                return await req.MakeResponseAsync(HttpStatusCode.NotImplemented);

            logger.LogInformation($"In DataStore.Enumerate, enumerate data in {tableClient.Name}, before = '{before}'");
            string query = $"PartitionKey eq '{userKey}'";
            if (!string.IsNullOrWhiteSpace(before))
                query += " and RowKey gt '" + before.Invert() + "'";
            // Determine which fieldNames to return
            List<string> fieldNames = new() { "RowKey", "DataLength" };
            if (storageClass.UseSummaryField) fieldNames.Add("Summary");
            // Find entries
            var returnedPages = tableClient.QueryAsync<DataFormat>(query, null, fieldNames);
            if (returnedPages == null)
                return await req.MakeResponseAsync(HttpStatusCode.NotFound);
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
                return await req.OkResponseAsync(licenseStore.GetTokenIfNew(userKey), JsonSerializer.Serialize(responseList,
                    new JsonSerializerOptions() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull })
                );
            }
        } 
        #endregion
    }
}