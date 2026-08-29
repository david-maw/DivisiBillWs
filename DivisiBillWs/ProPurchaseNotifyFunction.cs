using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace DivisiBillWs;

public class ProPurchaseNotifyFunction(ILogger<ProPurchaseNotifyFunction> logger, BlobContainerClient blobContainer)
{
    [Function("ProPurchaseNotify")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "delete", Route = "proPurchaseNotify/{userKey}")]
        HttpRequest httpRequest, string userKey)
    {
        var androidPurchase = await Authorization.ProLicenseFromRequestAsync(logger, httpRequest);
        if (string.IsNullOrEmpty(androidPurchase?.OrderId))
        {
            return new BadRequestObjectResult("Invalid purchase data, no OrderId in Purchase.");
        }
        string orderId = androidPurchase.OrderId;
        await blobContainer.CreateIfNotExistsAsync();
        //(userKey, orderId) = (orderId, userKey); // Swap values of userKey and orderId for testing 
        var blobsForOrderId = await GetBlobsWithPrefixAsync(orderId, httpRequest.HttpContext.RequestAborted);
        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (var blob in blobsForOrderId)
        {
            string newBlobName = $"{userKey}{blob.Name[orderId.Length..]}";
            await RenameBlobAsync(blob.Name, newBlobName, httpRequest.HttpContext.RequestAborted);
        }
        stopwatch.Stop();
        return new OkObjectResult($"OrderId: {orderId} ({blobsForOrderId.Count} blobs) to UserKey: {userKey} in {stopwatch.ElapsedMilliseconds} ms");
    }

    public async Task<List<BlobItem>> GetBlobsWithPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var results = new List<BlobItem>();

        await foreach (var blob in blobContainer.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
            prefix: prefix,
            cancellationToken: cancellationToken))
        {
            results.Add(blob);
        }

        return results;
    }

    /// <summary>
    /// Safely renames a blob by copying then deleting the original.
    /// Guarantees no data loss even if delete fails.
    /// </summary>
    public async Task RenameBlobAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        var source = blobContainer.GetBlobClient(oldName);
        var target = blobContainer.GetBlobClient(newName);

        // 1. Start copy
        await target.StartCopyFromUriAsync(source.Uri, cancellationToken: cancellationToken);

        // 2. Wait for copy to complete
        BlobProperties props;
        do
        {
            await Task.Delay(10, cancellationToken);
            props = await target.GetPropertiesAsync(cancellationToken: cancellationToken);
        }
        while (props.CopyStatus == CopyStatus.Pending);

        if (props.CopyStatus != CopyStatus.Success)
            throw new InvalidOperationException(
                $"Blob rename failed: copy status = {props.CopyStatus}");

        // 3. Delete original (safe: if this fails, both blobs still exist)
        try
        {
            await source.DeleteAsync(cancellationToken: cancellationToken);
        }
        catch
        {
            // Log or retry later — but do NOT delete the new blob.
            throw new InvalidOperationException(
                $"Blob copied to '{newName}' but failed to delete original '{oldName}'.");
        }
    }
}