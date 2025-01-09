using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using System.Diagnostics;

namespace DivisiBillWs;

internal static class AzureScan
{
    private static string GetString(this DocumentField field) => field.FieldType switch
    {
        DocumentFieldType.String => field.Value.AsString(),
        DocumentFieldType.Date => field.Value.AsDate().ToString("d"),
        DocumentFieldType.Time => field.Value.AsTime().ToString(),
        DocumentFieldType.PhoneNumber => field.Value.AsPhoneNumber().ToString(),
        DocumentFieldType.Double => field.Value.AsDouble().ToString(),
        DocumentFieldType.Int64 => field.Value.AsInt64().ToString(),
        DocumentFieldType.CountryRegion => field.Value.AsCountryRegion(),
        DocumentFieldType.Currency => field.Value.AsCurrency().ToString()!,
        _ => string.Empty,
    };
    internal static async Task<ScannedBill> CallScanStreamAsync(Stream s)
    {
        if (string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillCognitiveServicesEndpoint))
            throw new InvalidOperationException("Error in web service, no cognitive service endpoint value found, is DIVISIBILL_WS_COGNITIVE_SERVICES_EP set?");
        if (string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillCognitiveServicesKey))
            throw new InvalidOperationException("Error in web service, no cognitive service key value found, is DIVISIBILL_WS_COGNITIVE_SERVICES_KEY set?");
        AzureKeyCredential credential = new(Generated.BuildInfo.DivisiBillCognitiveServicesKey);
        var client = new DocumentAnalysisClient(new Uri(Generated.BuildInfo.DivisiBillCognitiveServicesEndpoint), credential);

        ScannedBill sb = new();
        AnalyzeDocumentOperation operation;

        using MemoryStream ms = new();
        s.CopyTo(ms);
        ms.Position = 0;

        try
        {
            operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-receipt", document: ms);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            throw;
        }
        AnalyzeResult result = await operation.WaitForCompletionAsync();

        for (int i = 0; i < result.Documents.Count; i++)
        {
            Debug.WriteLine($"Document {i}:");

            AnalyzedDocument document = result.Documents[i];

            foreach (var item in document.Fields)
            {
                Debug.WriteLine($"Fields.item {item.Key} is {item.Value.Value}, value = '{item.Value.GetString()}'");
                string valueString = item.Value.GetString();
                if (!string.IsNullOrEmpty(valueString))
                    sb.FormElements.Add(new FormElement() { FieldName = item.Key, FieldValue = valueString });
            }

            if (document.Fields.TryGetValue("Items", out DocumentField? itemsField))
            {
                if (itemsField.FieldType == DocumentFieldType.List)
                {
                    foreach (DocumentField itemField in itemsField.Value.AsList())
                    {
                        Debug.WriteLine("Item:");

                        if (itemField.FieldType == DocumentFieldType.Dictionary)
                        {
                            IReadOnlyDictionary<string, DocumentField> itemFields = itemField.Value.AsDictionary();

                            string? itemDescription = null;

                            if (itemFields.TryGetValue("Description", out DocumentField? itemDescriptionField))
                            {
                                if (itemDescriptionField.FieldType == DocumentFieldType.String)
                                {
                                    itemDescription = itemDescriptionField.Value.AsString();
                                    Debug.WriteLine($"  Description: '{itemDescription}', with confidence {itemDescriptionField.Confidence}");
                                }
                            }

                            double itemAmount = 0;

                            if (itemFields.TryGetValue("TotalPrice", out DocumentField? itemAmountField))
                            {
                                if (itemAmountField.FieldType == DocumentFieldType.Double)
                                {
                                    itemAmount = itemAmountField.Value.AsDouble();
                                    Debug.WriteLine($"  TotalPrice: '{itemAmount}', with confidence {itemAmountField.Confidence}");
                                }
                            }
                            if (!string.IsNullOrEmpty(itemDescription))
                                sb.OrderLines.Add(new OrderLine() { ItemName = itemDescription, ItemCost = itemAmount.ToString() });
                        }
                    }
                }
            }
        }
        return sb;
    }
}
