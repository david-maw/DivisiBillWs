using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DivisiBillWs;

public class FileFunction
{
    private readonly ILogger<FileFunction> logger;
    private readonly BlobContainerClient imagesBlobContainer;

    public FileFunction(ILogger<FileFunction> logger, BlobContainerClient imagesBlobContainerParam)
    {
        this.logger = logger;
        imagesBlobContainer = imagesBlobContainerParam;
    }

    /// <summary>
    /// Handles HTTP requests to upload, download, or delete a user-specific file in blob storage, based on the HTTP
    /// method provided.  
    /// </summary>
    /// <remarks>The operation is determined by the HTTP method: POST uploads a file, GET downloads a file,
    /// and DELETE removes a file. The file is scoped to the user identified by the 'userKey' in the request context. 
    /// Files whose names end in ".enc" are treated as encrypted versions of files without the 
    /// ".enc" suffix. Consequently we ensure that if an encrypted blob is created the corresponding plaintext blob is
    /// deleted and vice versa.
    /// If a file with the same name already exists during upload, it is moved to a 'deleted' folder before overwriting.
    /// The method supports optional file name routing and returns appropriate error responses for missing or invalid
    /// input.</remarks>
    /// <param name="httpRequest">The HTTP request containing the method, route data, and any uploaded file. The request must use the POST, GET,
    /// or DELETE method and may include a file in the form data for uploads.</param>
    /// <param name="fileName">The name of the file to download or delete. Required for GET and DELETE requests; optional for POST requests.</param>
    /// <returns>An IActionResult representing the outcome of the operation. Returns an OkObjectResult for successful uploads and
    /// deletions, a FileStreamResult for successful downloads, a BadRequestObjectResult for invalid input, or a
    /// NotFoundObjectResult if the requested file does not exist.</returns>
    [Function("file")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", "get", "delete", Route = "file/{fileName?}")] HttpRequest httpRequest,
        string fileName)
    {
        string? userKey = httpRequest.HttpContext.Items["userKey"] as string;
        
        // Local function to copy existing blob to deleted folder
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

        logger.LogInformation("'file' function processing a {Method} request for id {FileName}", httpRequest.Method, fileName);

        await imagesBlobContainer.CreateIfNotExistsAsync();

        switch (httpRequest.Method.ToUpper())
        {
            case "POST":
                {
                    var formFile = httpRequest.Form.Files["file"];
                    if (formFile == null)
                        return new BadRequestObjectResult("No file uploaded.");

                    string blobName = formFile.FileName;
                    if (string.IsNullOrWhiteSpace(blobName))
                        return new UnprocessableEntityObjectResult("No file name provided.");
                    logger.LogInformation("'file' function POST request is actually for form FileName {FileName}", blobName);
                    var uploadBlob = imagesBlobContainer.GetBlobClient(userKey + "/" + blobName);
                    if (await uploadBlob.ExistsAsync())
                    {
                        // A blob of that name already exists so copy it with a "deleted" prefix, removing any blob that is already deleted
                        var copyStatus = await CopyBlobToDeletedAsync(uploadBlob);
                        if (copyStatus != CopyStatus.Success)
                            return Utility.CreateFailedResult($"Copy deleted failed with status {copyStatus}.");
                    }
                    using var uploadStream = formFile.OpenReadStream();

                    // Set content type
                    var headers = new BlobHttpHeaders
                    {
                        ContentType = formFile.ContentType
                    };

                    // Upload with headers
                    var uploadResult = await uploadBlob.UploadAsync(uploadStream, headers);
                    if (uploadResult.GetRawResponse().IsError)
                        return Utility.CreateFailedResult("Upload failed.");
                    else
                    {
                        // Delete the alternate blob if there is one
                        string alternateBlobName = blobName.EndsWith(".enc") ? blobName[..^4] : blobName + ".enc";
                        var deleteAlternateBlob = imagesBlobContainer.GetBlobClient(userKey + "/" + alternateBlobName);
                        await deleteAlternateBlob.DeleteIfExistsAsync();
                        return new OkObjectResult($"Uploaded {blobName}");
                    }
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
                bool markedForDeletion = await deleteBlob.DeleteIfExistsAsync();
                if (markedForDeletion)
                    return new OkObjectResult($"Deleted {fileName}");
                else 
                    return new OkObjectResult($"Delete of {fileName} failed, file not found");

            default:
                return new BadRequestObjectResult("Unsupported HTTP method. Use POST, GET, or DELETE.");
        }
    }

    [Function("files")]
    public async Task<IActionResult> Files(
        [HttpTrigger(AuthorizationLevel.Function, "get", "delete", Route = "files")] HttpRequest httpRequest)
    {
        string? connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        await imagesBlobContainer.CreateIfNotExistsAsync();
        if (httpRequest.HttpContext.Items["userKey"] is not string userKey) throw new NullReferenceException(nameof(userKey));

        switch (httpRequest.Method.ToUpper())
        {
            case "GET":
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

            case "DELETE":
                int deleteCount = 0;
                int failCount = 0;
                List<Task> deleteTasks = [];

                // Local function to add blob delete tasks for a given prefix
                async Task AddUserBlobDeleteTasksAsync(string prefix)
                {
                    await foreach (var blobItem in imagesBlobContainer.GetBlobsAsync(prefix: prefix + "/"))
                    {
                        var blobClient = imagesBlobContainer.GetBlobClient(blobItem.Name);
                        deleteTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                await blobClient.DeleteIfExistsAsync();
                                Interlocked.Increment(ref deleteCount);
                            }
                            catch
                            {
                                Interlocked.Increment(ref failCount);
                            }
                        }));
                    }
                }

                // First delete all the active files for this user
                await AddUserBlobDeleteTasksAsync(userKey);
                // Now delete all the deleted files for this user
                await AddUserBlobDeleteTasksAsync("deleted/" + userKey);
                // Wait for all deletions to complete
                await Task.WhenAll(deleteTasks);
                // Return result
                return failCount > 0
                    ? Utility.CreateFailedResult($"Failed to delete {failCount} files, deleted {deleteCount} files.")
                    : new OkObjectResult($"Deleted {deleteCount} files.");

            default:
                return new BadRequestObjectResult("Unsupported HTTP method. Use GET, or DELETE.");
        } // switch
    }
}