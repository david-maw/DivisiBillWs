using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace DivisiBillWs;

public class ScanFunction
{
    private readonly ILogger logger;

    public ScanFunction(ILoggerFactory loggerFactory)
    {
        logger = loggerFactory.CreateLogger<ScanFunction>();
        licenseStore = new LicenseStore(logger);
        authorization = new(logger, licenseStore);
    }

    private readonly Authorization authorization;
    private readonly LicenseStore licenseStore;

    /// Beware, according to https://learn.microsoft.com/en-us/azure/azure-functions/functions-reference?tabs=blob#parallel-execution
    /// a the function code may be simultaneously executed on multiple threads.
    [Function("scan")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest httpRequest)
    {
        logger.LogInformation("The 'scan' web service function is processing a request.");

        string? userKey = await authorization.GetAuthorizedUserKeyAsync(httpRequest);
        if (userKey == null)
        {
            logger.LogInformation($"scan authorization failed, returning error");
            return new BadRequestResult();
        }

        // The passed data should be a multi-part form containing one file and an OCR license
        if (!httpRequest.HasFormContentType)
            return new BadRequestResult();
        IFormCollection forms = await httpRequest.ReadFormAsync();
        if (!forms.TryGetValue("license", out StringValues licenseValue))
            return new BadRequestResult();
        string ocrLicenseJson = licenseValue.ToString();
        AndroidPurchase? androidPurchase = AndroidPurchase.FromJson(ocrLicenseJson);
        if (forms.Files.Count != 1)
            return new BadRequestResult();


        // At this point we have all the data, so we can do authorization
        string? orderId = null;
        if (androidPurchase == null)
            return new BadRequestResult();
        if (await authorization.GetIsAuthorizedAsync(androidPurchase) && androidPurchase.GetIsLicenseFor(LicenseStore.OcrLicenseProductId))
            orderId = androidPurchase.OrderId;
        if (orderId == null)
            return new BadRequestResult();
        int scans = await licenseStore.GetScansAsync(orderId);
        if (scans < 0)
            return new BadRequestResult();
        else if (scans == 0) // The license is ok but has no associated scans left
            return new NoContentResult();
        Stream stream = httpRequest.Form.Files[0].OpenReadStream();
        // Get the options passed in the query string
        var query = httpRequest.Query;
        string option = query["option"].ToString() ?? "";
        if (string.IsNullOrWhiteSpace(option))
            logger.LogInformation("In 'scan', option is null");
        else
            logger.LogInformation("In 'scan', option = " + option);
        // Now do the actual scanning, or fake it
        try
        {
            switch (option)
            {
                case "":
                case "0": // The normal, production case
                case "2": // Test returning a fake bill
                    if (string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillCognitiveServicesEndpoint))
                        return new UnprocessableEntityObjectResult(
                            "Error in web service, no cognitive service endpoint value found, is DIVISIBILL_WS_COGNITIVE_SERVICES_EP set?");
                    if (string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillCognitiveServicesKey))
                        return new UnprocessableEntityObjectResult(
                            "Error in web service, no cognitive service key value found, is DIVISIBILL_WS_COGNITIVE_SERVICES_KEY set?");
                    try
                    {
                        ScannedBill sb = option == "0" ? await AzureScan.CallScanStreamAsync(stream) : fakeScannedBill;
                        sb.ScansLeft = await licenseStore.DecrementScansAsync(orderId);
                        return new JsonResult(sb, new System.Text.Json.JsonSerializerOptions() { DictionaryKeyPolicy = null });
                    }
                    catch (InvalidOperationException ex)
                    {
                        return new UnprocessableEntityObjectResult(ex.Message);
                    }
                case "1": // Test returning an error
                    return new BadRequestObjectResult($"""
                    Content: license
                    orderId={orderId}
                    Length = {stream.Length}
                    """);
                case "3": // Test fault behavior
                    throw new Exception("Test exception from Scan");
                default: // No idea what to do, so do nothing
                    return new BadRequestResult();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception in scan function");
            return new StatusCodeResult((int)HttpStatusCode.InternalServerError);
        }
    }

    private static readonly ScannedBill fakeScannedBill = new()
    {
        SourceName = "Fake Scan From DivisiBillWs",
        OrderLines =
        [
            new OrderLine() { ItemName="First Fake Item", ItemCost =   "1.00" },
            new OrderLine() { ItemName="Second Fake Item", ItemCost =  "2.00" },
            new OrderLine() { ItemName="Another Fake Item", ItemCost = "1.23" },
            new OrderLine() { ItemName="Another Fake Item", ItemCost = "1.23" },
            new OrderLine() { ItemName="Another Fake Item", ItemCost = "1.23" },
            new OrderLine() { ItemName="Another Fake Item", ItemCost = "1.23" },
            new OrderLine() { ItemName="Last Fake Item", ItemCost =    "1.00" },
        ],
        FormElements =
        [
            new FormElement(){ FieldName = "MerchantName", FieldValue = "King's Fish House Laguna Hills" },
            new FormElement(){ FieldName = "Subtotal", FieldValue = "9.02" },
            new FormElement(){ FieldName = "TransactionDate", FieldValue = "4/3/21" },
        ]
    };
}
