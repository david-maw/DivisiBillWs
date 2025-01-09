using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace DivisiBillWs;

public class ScannedBill
{
    public ScannedBill()
    {
    }
    #region Properties
    public string? SourceName { get; set; }
    /// <summary>
    /// Number of scans remaining on the license used
    /// </summary>
    public int ScansLeft { get; set; }
    public List<OrderLine> OrderLines { get; set; } = [];

    public List<FormElement> FormElements { get; set; } = [];
    #endregion

    #region Serialization 
    /// <summary>
    /// Handle serialization and deserialization of scan results so debugging need not require round trips to Google Textract
    /// </summary>
    private static readonly XmlSerializer itemsSerializer = new(typeof(ScannedBill));

    private void Serialize(Stream s)
    {
        using StreamWriter sw = new(s, Encoding.UTF8, 512, true);
        using var xmlwriter = XmlWriter.Create(sw, new XmlWriterSettings() { Indent = true, OmitXmlDeclaration = true, NewLineOnAttributes = true });
        var namespaces = new XmlSerializerNamespaces();
        namespaces.Add(string.Empty, string.Empty);
        itemsSerializer.Serialize(xmlwriter, this, namespaces);
    }
    private static ScannedBill Deserialize(Stream s)
    {
        using StreamReader sr = new(s, Encoding.UTF8, true, 512, true);
        using var xmlreader = XmlReader.Create(sr);
        return (ScannedBill)itemsSerializer.Deserialize(xmlreader)!;
    }
    #endregion
}

/// <summary>
/// Individual lines in each order
/// </summary>
public class OrderLine
{
    public string? ItemName { get; set; }
    public string? ItemCost { get; set; }

    /// <summary>
    /// Extract a decimal amount from a currency string. For now it handles just simple cases of dollar
    /// amounts (like "$12.34") or simple numbers like "1.23" or "12"  even if they are surrounded by other text.
    /// There are, of course arbitrarily more complex cases especially in other currencies, but we're not 
    /// going there for now, or even handling thousands separators (for example "$1,234.56" in dollar amounts).
    /// 
    /// The basic algorithm is to back up from the end of the string until you find a decimal separator followed
    /// by an appropriate number of digits or, failing that a single digit.
    /// 
    /// Then back up from there one character at a time until what's there is not a number - the last valid number 
    /// is what is returned.
    /// </summary>
    /// <param name="currencyText">A string representing an amount, for example $12.34" or "1.23"</param>
    /// <returns>A decimal representation of the amount and any leading text</returns>
    private static (decimal amount, string leadingtext) CurrencyStringToAmount(string currencyText)
    {
        if (string.IsNullOrEmpty(currencyText))
            return (0, string.Empty);
        NumberFormatInfo nfi = NumberFormatInfo.CurrentInfo;
        int currencyTextLen = currencyText.Length; // Total number field length
        int startInx = currencyTextLen - nfi.NumberDecimalDigits; // Working index of the first valid character in the number
        int endInx = -1; // the index of the last digit in the number

        while (startInx > 0 && ((startInx = currencyText.LastIndexOf(nfi.CurrencyDecimalSeparator, startInx - 1)) > 0))
        {
            // We found a decimal separator at startInx
            if (currencyTextLen - startInx - 1 >= nfi.NumberDecimalDigits)
            {
                int i;
                //There may be digits
                for (i = 0; i < nfi.NumberDecimalDigits; i++)
                {
                    if (!char.IsDigit(currencyText[startInx + 1 + i]))
                        continue;
                }
                endInx = startInx + i;
                break; // because if we got this far we have a decimal separator followed by enough digits
            }
        }
        if (endInx < 0) // we did not find a decimal separator followed by NumberDecimalDigits 
        {
            // Try for an integral number instead
            for (startInx = currencyTextLen - 1; startInx >= 0; startInx--)
            {
                if (char.IsDigit(currencyText[startInx]))
                {
                    endInx = startInx;
                    break;
                }
            }
        }

        // Now just keep parsing the number, backing up by 1 each time until it doesn't parse any more

        decimal result = 0;
        for (; startInx >= 0; startInx--)
        {
            if (decimal.TryParse(currencyText.Substring(startInx, endInx - startInx + 1), out decimal parsedNumber))
                result = parsedNumber;
            else
                break;
        }

        // Now figure out whether there is any leading text that ended up in the amount data, return it if there is

        string leadingText;
        if (endInx < 0)
            leadingText = currencyText;
        else if (startInx >= 0)
        {
            leadingText = currencyText[..(startInx + 1)].Trim();
            if (leadingText.EndsWith(nfi.CurrencySymbol)) // discard any trailing currency symbol
            {
                leadingText = leadingText[..^nfi.CurrencySymbol.Length].TrimEnd();
            }
        }
        else
            leadingText = string.Empty;
        return (result, leadingText);
    }
}
public class FormElement
{
    public string? FieldName { get; set; }
    public string? FieldValue { get; set; }

}

