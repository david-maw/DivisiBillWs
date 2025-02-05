using System.Text.Json;

namespace DivisiBillWs;

/// <summary>
/// An object describing either an Android license or a subscription. Originally delivered in JSON format
/// from various Android APIs, either via a call to the play store or from the Android Play API via DivisiBill. 
/// </summary>
public class AndroidPurchase
{
    public AndroidPurchase() { }
    public static AndroidPurchase? FromJson(string androidPurchaseJson)
    {
        return JsonSerializer.Deserialize<AndroidPurchase>(androidPurchaseJson,
                        new JsonSerializerOptions() { PropertyNameCaseInsensitive = true });
    }
    public static async Task<AndroidPurchase?> FromJsonAsync(Stream androidPurchaseJson)
    {
        return await JsonSerializer.DeserializeAsync<AndroidPurchase>(androidPurchaseJson,
                        new JsonSerializerOptions() { PropertyNameCaseInsensitive = true });
    }
    /// <summary>
    /// Check that the license if for a specified <see cref="ProductId"/>, has an <see cref="OrderId"/> and <see cref="PurchaseToken"/>, 
    /// and is for <see cref="LicenseStore.ExpectedPackageName"/> (DivisiBill).
    /// </summary>
    /// <param name="productId">The product name to check</param>
    /// <returns>True if this is a verifiable license for the productId</returns>
    public bool GetIsLicenseFor(string productId) =>
        !string.IsNullOrWhiteSpace(OrderId)
        && !string.IsNullOrWhiteSpace(PurchaseToken)
        && !string.IsNullOrWhiteSpace(PackageName)
        && !string.IsNullOrWhiteSpace(ProductId)
        && PackageName.Equals(LicenseStore.ExpectedPackageName) // only DivisiBill Licenses can be used
        && ProductId.Equals(productId);

    public string? PackageName { get; set; }
    public string? OrderId { get; set; }
    public string? ProductId { get; set; }
    public long PurchaseTime { get; set; }
    public int PurchaseState { get; set; }
    public required string PurchaseToken { get; set; }
    public string? ObfuscatedAccountId { get; set; }
    public int Quantity { get; set; }
    public bool Acknowledged { get; set; }
}