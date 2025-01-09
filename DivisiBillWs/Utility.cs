using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace DivisiBillWs;

internal static class Extensions
{
    internal static IActionResult OkResponseWithToken(this HttpRequest req, string? Token = null, string? responseText = null)
    {
        if (!string.IsNullOrEmpty(Token))
        {
            req.Headers[Authorization.TokenHeaderName] = Token;
        }

        if (!string.IsNullOrEmpty(responseText))
        {
            req.Headers["Content-Type"] = responseText.StartsWith("{")
                ? "application/json; charset=utf-8"
                : "text/plain; charset=utf-8";
            return new OkObjectResult(responseText);
        }
        return new OkResult();
    }
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
}
internal class Utility
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
}