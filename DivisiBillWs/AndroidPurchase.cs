using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DivisiBillWs;

/// <summary>
/// An object describing either an Android license or a subscription. Originally delivered in JSON format
/// from various Android APIs, either via a call to the play store or from the Android Play API via DivisiBill. 
/// </summary>
public class AndroidPurchase
{
    // Helper class to serialize subscriptions as the Play Store does (they have an extra node)
    class AndroidSubscription : AndroidPurchase
    {
        [JsonPropertyOrder(9)]
        [JsonPropertyName("autoRenewing")]
        public new bool? AutoRenewing
        {
            get => base.AutoRenewing;
            set => base.AutoRenewing = value;
        }

        // Deserialization constructor - System.Text.Json will bind JSON properties to these parameters.
        [JsonConstructor]
        public AndroidSubscription(
            string? orderId,
            string? packageName,
            string? productId,
            long purchaseTime,
            int purchaseState,
            string? purchaseToken,
            string? obfuscatedAccountId,
            int quantity,
            bool? autoRenewing,
            bool acknowledged)
        {
            OrderId = orderId;
            PackageName = packageName;
            ProductId = productId;
            PurchaseTime = purchaseTime;
            PurchaseState = purchaseState;
            PurchaseToken = purchaseToken;
            ObfuscatedAccountId = obfuscatedAccountId;
            Quantity = quantity;
            AutoRenewing = autoRenewing;
            Acknowledged = acknowledged;
        }
    }
    public AndroidPurchase() { }
    public static AndroidPurchase? FromJson(string androidPurchaseJson) => JsonSerializer.Deserialize<AndroidSubscription>(androidPurchaseJson);

    public static async Task<AndroidPurchase?> FromJsonAsync(Stream androidPurchaseJsonStream)
    {
        using var reader = new StreamReader(androidPurchaseJsonStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string result = await reader.ReadToEndAsync();
        return FromJson(result);
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

    public string ToJsonString()
    {
        if (IsLicense)
            return JsonSerializer.Serialize(this);
        else // Subscription
            return JsonSerializer.Serialize((AndroidSubscription)this);
    }

    // These properties are derived, never stored

    [JsonIgnore]
    public bool IsSubscription => AutoRenewing.HasValue;
    [JsonIgnore]
    public bool IsLicense => !IsSubscription;

    // The properties below are ordered and named to match the JSON structure sent by the Android Play Store
    // so that the signature of a serialized AndroidPurchase will match.

    [JsonPropertyOrder(1)]
    [JsonPropertyName("orderId")]
    public string? OrderId { get; set; }

    [JsonPropertyOrder(2)]
    [JsonPropertyName("packageName")]
    public string? PackageName { get; set; }

    [JsonPropertyOrder(3)]
    [JsonPropertyName("productId")]
    public string? ProductId { get; set; }

    [JsonPropertyOrder(4)]
    [JsonPropertyName("purchaseTime")]
    public long PurchaseTime { get; set; }

    [JsonPropertyOrder(5)]
    [JsonPropertyName("purchaseState")]
    public int PurchaseState { get; set; }

    [JsonPropertyOrder(6)]
    [JsonPropertyName("purchaseToken")]
    public string? PurchaseToken { get; set; }

    [JsonPropertyOrder(7)]
    [JsonPropertyName("obfuscatedAccountId")]
    public string? ObfuscatedAccountId { get; set; }

    [JsonPropertyOrder(8)]
    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    // Ignored as it is not present in license JSON returned by Play Store, just in subscriptions
    [JsonIgnore]
    [JsonPropertyOrder(9)]
    [JsonPropertyName("autoRenewing")]
    public bool? AutoRenewing { get; set; } = null;

    [JsonPropertyOrder(10)]
    [JsonPropertyName("acknowledged")]
    public bool Acknowledged { get; set; }

}