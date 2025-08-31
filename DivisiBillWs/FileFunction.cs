using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DivisiBillWs;

public class FileFunction
{
    private readonly ILogger<FileFunction> _logger;
    private readonly BlobContainerClient imagesBlobContainer;

    public FileFunction(ILogger<FileFunction> logger, BlobContainerClient imagesBlobContainerParam)
    {
        _logger = logger;
        imagesBlobContainer = imagesBlobContainerParam;
    }


    [Function("file")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", "get", "delete", Route = "file/{fileName?}")] HttpRequest httpRequest,
        string fileName)
    {
        string? userKey = httpRequest.HttpContext.Items["userKey"] as string;
#if DEBUG
        if (string.IsNullOrEmpty(userKey))
            userKey = "no-userKey-provided---so-use-this-fake-temporarily";
#endif
        async Task<CopyStatus> CopyBlobToDeletedAsync(BlobClient sourceBlob)
        {
            var deletedBlob = imagesBlobContainer.GetBlobClient("deleted/" + sourceBlob.Name);
            await deletedBlob.DeleteIfExistsAsync();
            var operation = await deletedBlob.StartCopyFromUriAsync(sourceBlob.Uri);

            await operation.WaitForCompletionAsync();
            // Wait briefly for server-side copy to complete
            BlobProperties props = await deletedBlob.GetPropertiesAsync();

            if (props.CopyStatus != CopyStatus.Success)
                await deletedBlob.DeleteIfExistsAsync(); // Who knows what state it's in, so delete it

            return props.CopyStatus;
        }

        // Beginning of function code

        await imagesBlobContainer.CreateIfNotExistsAsync();

        switch (httpRequest.Method.ToUpper())
        {
            case "POST":
                {
                    var formFile = httpRequest.Form.Files["file"];
                    if (formFile == null)
                        return new BadRequestObjectResult("No file uploaded.");

                    var uploadBlob = imagesBlobContainer.GetBlobClient(userKey + "/" + formFile.FileName);
                    if (await uploadBlob.ExistsAsync())
                    {
                        // A blob of that name already exists so copy it with a "deleted" prefix, removing any blob that is already deleted
                        var copyStatus = await CopyBlobToDeletedAsync(uploadBlob);
                        if (copyStatus != CopyStatus.Success)
                            return new ObjectResult($"Copy failed with status {copyStatus}.")
                            {
                                StatusCode = StatusCodes.Status500InternalServerError
                            };
                    }
                    using var uploadStream = formFile.OpenReadStream();

                    // Set content type
                    var headers = new BlobHttpHeaders
                    {
                        ContentType = formFile.ContentType
                    };

                    // Upload with headers
                    await uploadBlob.UploadAsync(uploadStream, headers);
                    return new OkObjectResult($"Uploaded {formFile.FileName}");
                }
            case "GET":
                if (string.IsNullOrEmpty(fileName))
                    return new BadRequestObjectResult("File name required.");

                var downloadBlob = imagesBlobContainer.GetBlobClient(userKey + "/" + fileName);

                var downloadResponse = await downloadBlob.DownloadAsync();
                if (downloadResponse.Value.Content == null)
                    return new NotFoundObjectResult("File not found or empty.");

                BlobProperties props = await downloadBlob.GetPropertiesAsync();

                return new FileStreamResult(downloadResponse.Value.Content, props.ContentType)
                {
                    FileDownloadName = fileName
                };

            case "DELETE":
                if (string.IsNullOrEmpty(fileName))
                    return new BadRequestObjectResult("File name required.");

                var deleteBlob = imagesBlobContainer.GetBlobClient(userKey + "/" + fileName);
                await deleteBlob.DeleteIfExistsAsync();
                return new OkObjectResult($"Deleted {fileName}");

            default:
                return new BadRequestObjectResult("Unsupported HTTP method. Use POST, GET, or DELETE.");
        }
    }

    [Function("files")]
    public async Task<IActionResult> ListFiles(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "files")] HttpRequest httpRequest)
    {
        string? connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");

        string? userKey = httpRequest.HttpContext.Items["userKey"] as string;
#if DEBUG
        if (string.IsNullOrEmpty(userKey))
            userKey = "no-userKey-provided---so-use-this-fake-temporarily";
#endif

        await imagesBlobContainer.CreateIfNotExistsAsync();

        int prefixLength = userKey.Length + 1; // The "+1" is for the delimiter "/"
        var list = new List<object>();
        await foreach (var blob in imagesBlobContainer.GetBlobsAsync(prefix: userKey + "/"))
        {
            list.Add(new
            {
                name = blob.Name.Substring(prefixLength),
                contentType = blob.Properties.ContentType,
                size = blob.Properties.ContentLength,
                lastModified = blob.Properties.LastModified
            });
        }

        return new OkObjectResult(list);
    }
}