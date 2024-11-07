using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using System.Diagnostics;

namespace DivisiBillWs;

internal static class AzureScan
{
    private static string GetString(this DocumentField field)
    {
        switch (field.FieldType)
        {
            case DocumentFieldType.String:
                return field.Value.AsString();
            case DocumentFieldType.Date:
                return field.Value.AsDate().ToString("d");
            case DocumentFieldType.Time:
                return field.Value.AsTime().ToString();
            case DocumentFieldType.PhoneNumber:
                return field.Value.AsPhoneNumber().ToString();
            case DocumentFieldType.Double:
                return field.Value.AsDouble().ToString();
            case DocumentFieldType.Int64:
                return field.Value.AsInt64().ToString();
            case DocumentFieldType.CountryRegion:
                return field.Value.AsCountryRegion();
            case DocumentFieldType.Currency:
                return field.Value.AsCurrency().ToString()!;
            default:
                return String.Empty;
        }
    }
    internal static async Task<ScannedBill> CallScanStreamAsync(Stream s)
    {
        if (string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillCognitiveServicesEndpoint))
            throw new InvalidOperationException("Error in web service, no cognitive service endpoint value found, is DIVISIBILL_WS_COGNITIVE_SERVICES_EP set?");
        if (string.IsNullOrEmpty(Generated.BuildInfo.DivisiBillCognitiveServicesKey))
            throw new InvalidOperationException("Error in web service, no cognitive service key value found, is DIVISIBILL_WS_COGNITIVE_SERVICES_KEY set?");
        AzureKeyCredential credential = new AzureKeyCredential(Generated.BuildInfo.DivisiBillCognitiveServicesKey);
        var client = new DocumentAnalysisClient(new Uri(Generated.BuildInfo.DivisiBillCognitiveServicesEndpoint), credential);

        ScannedBill sb = new ScannedBill();
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
