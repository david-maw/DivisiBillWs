using System.Text.Json;

namespace DivisiBillWs;

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
    public bool GetIsLicenseFor(string productId) =>
        !string.IsNullOrWhiteSpace(OrderId)
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