using System.Text;

namespace DivisiBillWs
{
    internal static class Extensions
    {
        internal static async Task<HttpResponseData> OkResponseAsync(this HttpRequestData req, string? Token = null, string? responseText = null)
        {
            var response = req.CreateResponse(HttpStatusCode.OK);
            if (!string.IsNullOrEmpty(Token))
                response.Headers.Add(Authorization.TokenHeaderName, Token);
            if (!string.IsNullOrEmpty(responseText))
            {
                if (responseText.StartsWith("{"))
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                else
                    response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
                await response.WriteStringAsync(responseText);
            }
            return response;
        }
        internal static async Task<HttpResponseData> MakeResponseAsync(this HttpRequestData req, HttpStatusCode httpStatusCode, string? responseText = null)
        {
            var response = req.CreateResponse(httpStatusCode);
            if (!string.IsNullOrEmpty(responseText))
            {
                if (responseText.StartsWith("{"))
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                else
                    response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
                await response.WriteStringAsync(responseText);
            }
            return response;
        }
        internal static string Invert(this string input)
        {
            char[] chars = input.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c >= '0' && c <= '9')
                    chars[i] = (char)((int)('0') + (int)('9') - (int)c);
                else
                    chars[i] = c;
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
                if (c >= '0' && c <= '9')
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
            StringBuilder randomString = new StringBuilder();

            Random random = new Random();

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
}