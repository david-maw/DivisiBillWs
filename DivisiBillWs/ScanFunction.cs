using Google.Apis.AndroidPublisher.v3.Data;
using HttpMultipartParser;
using System.Text.Json;

namespace DivisiBillWs
{
    public class ScanFunction
    {
        private readonly ILogger logger;

        public ScanFunction(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger<ScanFunction>();
            licenseStore = new LicenseStore(logger);
            authorization = new(logger, licenseStore);
        }

        Authorization authorization;

        LicenseStore licenseStore;

        /// Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
        /// a the function code may be simultaneously executed on multiple threads.
        [Function("scan")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            logger.LogInformation("The 'scan' web service function is processing a request.");

            // Get the image data stream
            // Old style calls passed the license in the body, new style ones use headers and the entire body is data
            
            Nullable<KeyValuePair<string, IEnumerable<string>>> contentTypeList = req.Headers.FirstOrDefault(h => h.Key.Equals("Content-Type"));
            bool multipart = false;
            Stream? stream = null;
            string? androidPurchaseString = null;
            if (contentTypeList.HasValue)
            {
                string? firstContentType = contentTypeList.Value.Value.FirstOrDefault();
                if (firstContentType != null && firstContentType.StartsWith("multipart"))
                    multipart = true;
            }
            if (multipart)
            {
                MultipartFormDataParser parsedFormBody = await MultipartFormDataParser.ParseAsync(req.Body);
                FilePart file = parsedFormBody.Files[0];
                // Make sure the file is valid looking
                if (file is null || file.Data.Length <= 1000) // Nothing smaller could reasonably be an image
                {
                    logger.LogError("In 'scan', no (or insufficient) image data in file");
                    return await req.MakeResponseAsync(HttpStatusCode.BadRequest);
                }
                stream = file.Data;
                androidPurchaseString = parsedFormBody.GetParameterValue("license");
            }
            else
            {
                stream = req.Body;
            }
            // At this point we have all the data, so we can do authorization
            string? orderId = null;
            if (string.IsNullOrEmpty(androidPurchaseString))
                return await req.MakeResponseAsync(HttpStatusCode.BadRequest);
            else
            {
                AndroidPurchase? androidPurchase = AndroidPurchase.FromJson(androidPurchaseString);
                if (androidPurchase == null)
                    return await req.MakeResponseAsync(HttpStatusCode.BadRequest);
                if (await authorization.GetIsAuthorizedAsync(androidPurchase) && androidPurchase.GetIsLicenseFor(LicenseStore.OcrLicenseProductId))
                    orderId = androidPurchase.OrderId;
                if (orderId == null)
                    return await req.MakeResponseAsync(HttpStatusCode.BadRequest);
            }
            int scans = await licenseStore.GetScansAsync(orderId);
            if (scans < 0)
                return await req.MakeResponseAsync(HttpStatusCode.BadRequest);
            else if (scans == 0) // The license is ok but has no associated scans left
                return await req.MakeResponseAsync(HttpStatusCode.NoContent);
            // Get the options passed in the query string
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            string? option = query["option"];
            if (option == null)
                logger.LogInformation("In 'scan', option is null");
            else
                logger.LogInformation("In 'scan', option = " + option);
            // Now do the actual scanning (or fake it)
            if (string.IsNullOrWhiteSpace(option) || option == "0") // normal case
            {
                try
                {
                    var response = await req.OkResponseAsync();
                    ScannedBill sb = await AzureScan.CallScanStreamAsync(stream);
                    sb.ScansLeft = (orderId.Equals("Fake-OrderId")) ? 4
                        : await licenseStore.DecrementScansAsync(orderId);
                    await response.WriteStringAsync(JsonSerializer.Serialize(sb));
                    return response;
                }
                catch (InvalidOperationException ex)
                {
                    return await req.MakeResponseAsync(HttpStatusCode.UnprocessableContent, ex.Message);
                }
            }
            else if (option == "1") // quick round trip test
            {
                return await req.MakeResponseAsync(HttpStatusCode.BadRequest, $"""
                    Content: license
                    orderId={orderId}

                    Multi part: {multipart}
                    Length = {stream.Length}
                    """);
            }
            else if (option == "2") // don't scan the image, just return a fake scan result
            {
                if (string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillCognitiveServicesEndpoint))
                    return await req.MakeResponseAsync(HttpStatusCode.UnprocessableContent,
                        "Error in web service, no cognitive service endpoint value found, is DIVISIBILL_WS_COGNITIVE_SERVICES_EP set?");
                if (string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillCognitiveServicesKey))
                    return await req.MakeResponseAsync(HttpStatusCode.UnprocessableContent,
                        "Error in web service, no cognitive service key value found, is DIVISIBILL_WS_COGNITIVE_SERVICES_KEY set?");

                var response = await req.OkResponseAsync();
                fakeScannedBill.ScansLeft = (orderId.Equals("Fake-OrderId")) ? 4
                    : await licenseStore.DecrementScansAsync(orderId);
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                await response.WriteStringAsync(JsonSerializer.Serialize(fakeScannedBill));
                return response;
            }
            else if (option == "3")
                throw new Exception("Test exception from Scan");
            else
                return await req.MakeResponseAsync(HttpStatusCode.BadRequest);
        }

        private static ScannedBill fakeScannedBill = new ScannedBill()
        {
            SourceName = "Fake Scan From DivisiBillWs",
            OrderLines = new System.Collections.Generic.List<OrderLine>
            {
                new OrderLine() { ItemName="First Fake Item", ItemCost =   "1.00" },
                new OrderLine() { ItemName="Second Fake Item", ItemCost =  "2.00" },
                new OrderLine() { ItemName="Another Fake Item", ItemCost = "1.23" },
                new OrderLine() { ItemName="Another Fake Item", ItemCost = "1.23" },
                new OrderLine() { ItemName="Another Fake Item", ItemCost = "1.23" },
                new OrderLine() { ItemName="Another Fake Item", ItemCost = "1.23" },
                new OrderLine() { ItemName="Last Fake Item", ItemCost =    "1.00" },
            },
            FormElements = new System.Collections.Generic.List<FormElement>
            {
                new FormElement(){ FieldName = "MerchantName", FieldValue = "King's Fish House Laguna Hills" },
                new FormElement(){ FieldName = "Subtotal", FieldValue = "9.02" },
                new FormElement(){ FieldName = "TransactionDate", FieldValue = "4/3/21" },
            }
        };
    }
}
