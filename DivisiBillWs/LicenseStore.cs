using Azure;
using Azure.Data.Tables;

namespace DivisiBillWs
{
    internal class LicenseStore
    {
        private class TokenInfo : ITableEntity
        {
            // Purchase data
            public DateTime TimeExpired { get; set; } = default!; // Set when creating item
            public string ProOrderId { get; set; } = default!; // The ProOrderId that matches this token

            // Required for ITableEntity
            public string RowKey { get; set; } = default!; // User must provide a value
            public string PartitionKey { get; set; } = LicenseStore.PartitionKeyName; // User must provide a value
            public ETag ETag { get; set; } = default!; // Value optional
            public DateTimeOffset? Timestamp { get; set; } = default!; // Set by system whenever item is changed
        }
        private class PurchaseInfo : ITableEntity
        {
            // Purchase data
            public string ProductId { get; set; } = default!;
            public int ScansLeft { get; set; } = default;
            public DateTimeOffset TimeCreated { get; set; } = DateTime.Now; // Set when creating item
            public string ObfuscatedAccountId { get; set; } = default!;
            public DateTimeOffset TimeUsed { get; set; } = default; // Set when using this license

            // Required for ITableEntity
            public string RowKey { get; set; } = default!; // User must provide a value
            public string PartitionKey { get; set; } = LicenseStore.PartitionKeyName; // User must provide a value
            public ETag ETag { get; set; } = default!; // Value optional
            public DateTimeOffset? Timestamp { get; set; } = default!; // Set by system whenever item is changed
        }
        public const string ProSubscriptionId = "pro.subscription";
        public const string ProSubscriptionIdOld = "pro.upgrade";
        public const string OcrLicenseProductId = "ocr.calls";
        public const int OcrLicenseScans = 30;
        public const string ExpectedPackageName = "com.autoplus.divisibill";
#if DEBUG
        const string TableName = "DivisiBillDebugLicenses";
        const string TokenTableName = "DivisiBillDebugTokens";
#else
        const string TableName = "DivisiBillLicenses";
        const string TokenTableName = "DivisiBillTokens";
#endif
        internal const string PartitionKeyName = "DivisiBill";
        static readonly string connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage")!; 

        static readonly TableServiceClient tableServiceClient = new TableServiceClient(connectionString);

        // New instance of TableClient class referencing the server-side table
        static readonly TableClient tableClient = tableServiceClient.GetTableClient(TableName);
        static readonly TableClient tokenTable = tableServiceClient.GetTableClient(TokenTableName);

        static LicenseStore()
        {
            var resp = tableClient.CreateIfNotExists();
            resp = tokenTable.CreateIfNotExists();
        }

        ILogger logger;
        internal LicenseStore(ILogger loggerParam)
        {
            logger = loggerParam;
        }
        private bool IsExpiring(DateTime expirationTime)
        {
            return (expirationTime.ToLocalTime() - DateTime.Now) < TimeSpan.FromSeconds(5);
        }
        /// <summary>
        /// Return the new token if the old one is expired, otherwise null
        /// </summary>
        /// <returns>Null if the current token is fine for future use, a token if a new one is needed</returns>
        public string? GetTokenIfNew(string userKey)
        {
            ArgumentException.ThrowIfNullOrEmpty(userKey);

            var proTokenInfo = tokenTable.Query<TokenInfo>(ti => ti.ProOrderId == userKey).FirstOrDefault();

            if (proTokenInfo != null)
            {
                if (IsExpiring(proTokenInfo.TimeExpired))
                {
                    logger.LogInformation($"In LicenseStore.GetTokenIfNew for {userKey} found {tokenTable.Name}[{PartitionKeyName}, {proTokenInfo.RowKey}] expiring, " +
                        "returning a replacement");
                    if (InternalClearToken(userKey, proTokenInfo))
                        return GenerateToken(userKey);
                    else
                        logger.LogInformation($"In LicenseStore.GetTokenIfNew for {userKey} found {tokenTable.Name}[{PartitionKeyName}, {proTokenInfo.RowKey}] but it went away, " +
                            "returning null");
                }
                else
                    logger.LogInformation($"In LicenseStore.GetTokenIfNew for {userKey} found {tokenTable.Name}[{PartitionKeyName}, {proTokenInfo.RowKey}] is not expiring, " +
                        "returning null");
            }
            else // There is no stored token
            {
                logger.LogInformation($"In LicenseStore.GetTokenIfNew for {userKey} not found in {tokenTable.Name}, returning a new one");
                return GenerateToken(userKey);
            }
            return null;
        }

        /// <summary>
        /// Delete any existing token
        /// </summary>
        /// Returns true if the token was cleared.
        /// <exception cref="ApplicationException"></exception>
        private bool InternalClearToken(string userKey, TokenInfo oldTokenInfo) 
        {
            var response = tokenTable.DeleteEntity(oldTokenInfo.PartitionKey, oldTokenInfo.RowKey);
            if (response == null || response.IsError)
            {
                // Possibly another thread already removed it, so only report an error if it is not still there
                if (tokenTable.Query<TokenInfo>(ti => ti.PartitionKey.Equals(oldTokenInfo.PartitionKey) && ti.RowKey.Equals(oldTokenInfo.RowKey) && ti.ProOrderId == userKey).FirstOrDefault() == null)
                    return false; // the token has gone, removed by another thread perhaps, but in any event, not by us
                else
                {
                    string responseInfo = (response != null) ? ", response = " + response.ToString() : "";
                    logger.LogError($"In LicenseStore.ClearToken for {userKey} found {tokenTable.Name}[{PartitionKeyName}, {oldTokenInfo.RowKey}], unable to remove it, throwing exception");
                    throw new ApplicationException("Token Removal Failed" + responseInfo); 
                }
            }
            return true;
        }
        /// <summary>
        /// Create a token and insert it in the table
        /// </summary>
        /// <returns>The token</returns>
        /// <exception cref="ApplicationException">Throws if the token was not inserted in the token table</exception>
        private string GenerateToken(string userKey)
        {
            ArgumentException.ThrowIfNullOrEmpty(userKey);

            string Token = Utility.GenerateToken();

            TokenInfo info = new TokenInfo() { PartitionKey = PartitionKeyName, RowKey = Token, 
                ProOrderId = userKey, TimeExpired = DateTime.UtcNow + TimeSpan.FromSeconds(60)};

            var response = tokenTable.UpsertEntity(info);
            if (!response.IsError)
            {
                logger.LogInformation($"In LicenseStore.GenerateToken, {tokenTable.Name}[{info.PartitionKey}, {info.RowKey}] has value, returning " + Token);
                return Token;
            }
            else
            {
                logger.LogError($"In LicenseStore.GenerateToken, {tokenTable.Name}[{info.PartitionKey}, {info.RowKey}] not inserted, throwing exception");
                throw new ApplicationException("incomingToken generation failed");
            }
        }
        public string? GetUserKeyFromToken(string? incomingToken)
        {
            if (string.IsNullOrEmpty(incomingToken))
                return null;

            NullableResponse<TokenInfo> tokenInfoResponse = tokenTable.GetEntityIfExists<TokenInfo>(
                partitionKey: PartitionKeyName,
                rowKey: incomingToken
                );
            if (tokenInfoResponse.HasValue)
            {
                if (tokenInfoResponse!.Value!.TimeExpired > DateTime.UtcNow)
                {
                    // The purchase was already validated with the app store, now we know it's one of ours
                    logger.LogInformation($"In LicenseStore.GetUserKeyFromToken, {tokenTable.Name}[{PartitionKeyName}, {incomingToken}] is current, returning true");
                    // This means we can get the proLicense value from the token table
                    return tokenInfoResponse.Value.ProOrderId;
                }
                else
                    logger.LogInformation($"In LicenseStore.GetUserKeyFromToken, {tokenTable.Name}[{PartitionKeyName}, {incomingToken}] is not current, returning false");
            }
            else
                logger.LogInformation($"In LicenseStore.GetUserKeyFromToken, {tokenTable.Name}[{PartitionKeyName}, {incomingToken}] not found, returning false");
            return null;
        }
        /// <summary>
        /// Validate a license by making sure it is known (by its ProOrderId). 
        /// </summary>
        /// <param name="OrderId">The unique key that distinguishes license instances</param>
        /// <returns>The number of scans remaining for this license or -1 if the license was not found</returns>
        public async Task<int> GetScansAsync(string OrderId)
        {
            ArgumentException.ThrowIfNullOrEmpty(OrderId);

            NullableResponse<PurchaseInfo> purchaseInfoResponse = await tableClient.GetEntityIfExistsAsync<PurchaseInfo>(
                rowKey: OrderId,
                partitionKey: PartitionKeyName
                );
            if (purchaseInfoResponse.HasValue)
            {
                logger.LogInformation($"In LicenseStore.GetScans, {tableClient.Name}[{PartitionKeyName}, {OrderId}] has value, returning " + purchaseInfoResponse!.Value!.ScansLeft);
                return purchaseInfoResponse!.Value!.ScansLeft;
            }
            else
            {
                logger.LogInformation($"In LicenseStore.GetScans, {tableClient.Name}[{PartitionKeyName}, {OrderId}] not found, returning error");
                return -1;
            }
        }
        /// <summary>
        /// Record a new license, making sure it is not already known (by its OrderId) 
        /// </summary>
        /// <param name="ProductId">The product ID - used to determine how many scans a new license is created with</param>
        /// <param name="OrderId">The unique key that distinguishes license instances</param>
        /// <returns>True if the license was recorded, false if it was already known</returns>
        public async Task<bool> RecordAsync(string ProductId, string OrderId, int quantity, string obfuscatedAccountId)
        {
            ArgumentException.ThrowIfNullOrEmpty(ProductId);
            ArgumentException.ThrowIfNullOrEmpty(OrderId);

            NullableResponse<PurchaseInfo> purchaseInfoResponse = tableClient.GetEntityIfExists<PurchaseInfo>(
                rowKey: OrderId,
                partitionKey: PartitionKeyName
                );
            if (purchaseInfoResponse.HasValue)
            {
                logger.LogInformation($"In LicenseStore.Record, {tableClient.Name}[{PartitionKeyName}, {OrderId}] has value, returning error");
                return false;
            }
            else
            {
                logger.LogInformation($"In LicenseStore.Record, {tableClient.Name}[{PartitionKeyName}, {OrderId}] does not exist, adding it");

                int totalScansLeft = 0;
                // Create a new entry
                PurchaseInfo purchaseInfo = new()
                {
                    RowKey = OrderId,
                    ProductId = ProductId,
                    ObfuscatedAccountId = obfuscatedAccountId,
                    TimeCreated = DateTime.UtcNow,
                };
                List<TableTransactionAction> batch = new();
                if (ProductId.Equals(OcrLicenseProductId)) // This is an OCR license, so add up the counts remaining on previous ones and apply them to the new one
                {
                    // Count up the existing licenses
                    var v = tableClient.QueryAsync<PurchaseInfo>(r => r.ObfuscatedAccountId.Equals(obfuscatedAccountId) && r.ScansLeft > 0);

                    await foreach (var row in v)
                        totalScansLeft += row.ScansLeft;
                    purchaseInfo.ScansLeft = totalScansLeft + OcrLicenseScans * int.Max(quantity, 1);
                    // Now add zeroing out the scan counts to the transaction
                    await foreach (var row in v)
                    {
                        row.ScansLeft = 0;
                        batch.Add(new TableTransactionAction(TableTransactionActionType.UpdateMerge, row));
                    }
                }
                bool completedOk = false;
                if (batch.Count > 0) // There are some OCR record updates so make sure all or none succeed
                {
                    batch.Add(new TableTransactionAction(TableTransactionActionType.Add, purchaseInfo));
                    try
                    {
                        await tableClient.SubmitTransactionAsync(batch);
                        completedOk = true;
                    }
                    catch (Exception)
                    {
                        purchaseInfo.ScansLeft -= totalScansLeft; // because we did not zero all those records
                    } 
                }
                // Either there were no existing records to update so there's just the one transaction and no need for a batch
                // or the batch failed.
                if (!completedOk)
                {
                    var v = await tableClient.AddEntityAsync(purchaseInfo);
                    completedOk = !v.IsError;
                }
                return completedOk;
            }
        }
        public async Task<int> DecrementScansAsync(string orderId)
        {
            ArgumentException.ThrowIfNullOrEmpty(orderId);

            logger.LogInformation($"In DecrementScans, orderId = {orderId}");

            NullableResponse<PurchaseInfo> purchaseInfoResponse = await tableClient.GetEntityIfExistsAsync<PurchaseInfo>(
                rowKey: orderId,
                partitionKey: PartitionKeyName
                );
            if (purchaseInfoResponse.HasValue && purchaseInfoResponse.Value is PurchaseInfo purchaseInfo && purchaseInfo.ScansLeft > 0)
            {
                purchaseInfo.TimeUsed = DateTime.UtcNow;
                purchaseInfo.ScansLeft--;
                await tableClient.UpdateEntityAsync(purchaseInfo,purchaseInfo.ETag);
                return purchaseInfo.ScansLeft;
            }
            else
                return 0;
        }
        public async Task<bool> UpdateTimeUsedAsync(string orderId)
        {
            ArgumentException.ThrowIfNullOrEmpty(orderId);

            logger.LogInformation($"In UpdateImeUsed, orderId = {orderId}");

            NullableResponse<PurchaseInfo> purchaseInfoResponse = await tableClient.GetEntityIfExistsAsync<PurchaseInfo>(
                rowKey: orderId,
                partitionKey: PartitionKeyName
                );
            if (purchaseInfoResponse.HasValue && purchaseInfoResponse.Value is PurchaseInfo purchaseInfo)
            {
                purchaseInfo.TimeUsed = DateTime.UtcNow;
                await tableClient.UpdateEntityAsync(purchaseInfo, purchaseInfo.ETag);
                return true;
            }
            return false;
        }
    }
}