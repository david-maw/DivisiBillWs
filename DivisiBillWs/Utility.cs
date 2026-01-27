using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace DivisiBillWs;

internal static class Extensions
{
    /// <summary>
    /// Complement strings of decimal digits, for example "12345" => "98765"
    /// </summary>
    /// <param name="input">String to convert</param>
    /// <returns>converted string</returns>
    internal static string Invert(this string input)
    {
        char[] chars = input.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            chars[i] = c is >= '0' and <= '9' ? (char)('0' + '9' - c) : c;
        }
        return new string(chars);
    }

    /// <summary>
    /// Validates that a string is a 14 digit integer name intended to store yyyymmddhhmmss 
    /// however the only constant on the number is that yyyy be <= 3000
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    internal static bool IsValidName(this string input)
    {
        if (input.Length != 14)
            return false;
        char[] chars = input.ToCharArray();
        if (chars[0] > '3')
            return false;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c is >= '0' and <= '9')
                continue;
            else
                return false;
        }
        return true;
    }
    internal static DateTime ToDateTime(this string rowKey)
    {
        string yyyymmddhhmmss = rowKey.Invert();
        return Utility.DateTimeFromyyyymmddhhmmss(yyyymmddhhmmss);
    }
}
internal static class Utility
{
#if DEBUG
    public static readonly bool IsDebug = true; // Not a const so as to avoid "unreachable code" warnings
#else
    public static readonly bool IsDebug = false;
#endif
    internal static string GenerateToken(int size = 50)
    {
        StringBuilder randomString = new();

        Random random = new();

        // String that contain both alphabets and numbers
        string digits = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        for (int i = 0; i < size; i++)
        {

            // Selecting a index randomly
            int x = random.Next(digits.Length);

            // Appending the character at the 
            // index to the random alphanumeric string.
            randomString.Append(digits[x]);
        }
        return randomString.ToString();
    }

    internal static ObjectResult CreateFailedResult
        (string errorMessage, int statusCode = StatusCodes.Status500InternalServerError)
        => new(errorMessage) { StatusCode = statusCode };


    /// <summary>
    /// Extracts the value of a specified top-level property from a JSON string.
    /// </summary>
    /// <remarks>If the JSON is malformed or the specified property does not exist at the root level, the
    /// method returns <see langword="null"/>. Only top-level properties are considered; nested properties are not
    /// searched.</remarks>
    /// <param name="json">The JSON string to parse. Must not be null, empty, or contain only whitespace.</param>
    /// <param name="nodeName">The name of the top-level property to extract from the JSON. Must not be null, empty, or contain only
    /// whitespace.</param>
    /// <returns>A <see cref="System.Text.Json.JsonElement"/> representing the value of the specified property if found;
    /// otherwise, <see langword="null"/>.</returns>
    public static JsonElement? ExtractNode(string json, string nodeName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(nodeName))
            return null;

        try
        {
            JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty(nodeName, out JsonElement node))
            {
                return node;
            }
        }
        catch (JsonException)
        {
            // Handle malformed JSON
        }

        return null;
    }
    internal static DateTime DateTimeFromyyyymmddhhmmss(string yyyymmddhhmmss)
    {
        string s = Path.GetFileNameWithoutExtension(yyyymmddhhmmss);
        if (s.Length == 14
            && int.TryParse(s[..4], out int y)
            && y > 2010 && y < 2030
            && int.TryParse(s.AsSpan(4, 2), out int m)
            && m >= 1 && m <= 12
            && int.TryParse(s.AsSpan(6, 2), out int d)
            && d >= 1 && d <= 31
            && int.TryParse(s.AsSpan(8, 2), out int hh)
            && hh >= 0 && hh <= 23
            && int.TryParse(s.AsSpan(10, 2), out int mm)
            && mm >= 0 && mm < 60
            && int.TryParse(s.AsSpan(12, 2), out int ss)
            && ss >= 0 && ss < 60)
            return new DateTime(y, m, d, hh, mm, ss); // Plausible date
        else
            return DateTime.MinValue;
    }
}