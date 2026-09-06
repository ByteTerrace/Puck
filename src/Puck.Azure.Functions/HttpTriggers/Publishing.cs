using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using System.Net;
using Puck.Azure.Functions.Services;

namespace Puck.Azure.Functions.HttpTriggers;

public sealed class Publishing(
    IConfiguration configuration,
    IPartitionResolver partitionResolver,
    IUserCredentialContext userCredentialContext,
    IUserStorageLocationService userStorageLocationService
)
{
    // Published files are ANCHORED: they live under public/ in the user's oid-named container on
    // the anchor partition — Front Door's only blob origin — regardless of which partition the
    // user's home (private/, system/) is on. Front Door rewrites /public/<tenant>/<path> to
    // /<tenant>/public/<path> and reads with its own identity, so published URLs survive
    // migrations and Front Door never needs per-partition routing. The user's private/ home is
    // resolved through the grain (authoritative during migrations), never computed locally.
    // Because a user owns both prefixes of any container named after their oid, every step here is
    // a plain on-behalf-of operation — no host identity, no SAS.
    private const string DefaultPublicBaseUrl = "https://byteterrace.com/public";
    private const string PrivatePrefix = "private/";
    private const string PublicPrefix = "public/";

    public sealed class PublishRequest
    {
        public string? BlobName { get; set; }
    }

    private static string? GetDelegatedUserObjectId(FunctionContext functionContext) {
        var user = functionContext
            .GetHttpContext()!
            .User;
        var hasScopes = user
            .Claims
            .Any(predicate: static claim =>
                ("scp" == claim.Type) ||
                ("http://schemas.microsoft.com/identity/claims/scope" == claim.Type)
            );

        return hasScopes
            ? user
                .Identity
                ?.Name
                ?.ToLowerInvariant()
            : null;
    }

    private BlobContainerClient GetAnchorContainerClient(string userObjectId) =>
        new(
            blobContainerUri: new Uri(
                baseUri: partitionResolver.GetBlobEndpoint(partition: Constants.AnchorPartition),
                relativeUri: userObjectId
            ),
            credential: userCredentialContext.UserContext
        );
    private BlobContainerClient GetHomeContainerClient(
        string blobEndpoint,
        string userObjectId
    ) =>
        new(
            blobContainerUri: new Uri(
                baseUri: new(uriString: blobEndpoint),
                relativeUri: userObjectId
            ),
            credential: userCredentialContext.UserContext
        );
    private string GetPublicBaseUrl() =>
        (configuration.GetValue<string>(key: "Publishing:PublicBaseUrl") ?? DefaultPublicBaseUrl).TrimEnd('/');
    private static async Task<HttpResponseData> CreateMigratingResponseAsync(
        HttpRequestData httpRequestData,
        CancellationToken cancellationToken
    ) {
        var response = httpRequestData.CreateResponse();

        response.StatusCode = HttpStatusCode.Conflict;

        await response.WriteAsJsonAsync(
            cancellationToken: cancellationToken,
            instance: new {
                Detail = "Your storage is being migrated; try again in a few minutes.",
            }
        );

        return response;
    }
    private static (string RelativePath, HttpResponseData? Error) ParseBlobName(
        HttpRequestData httpRequestData,
        PublishRequest? request
    ) {
        var blobName = request?.BlobName?.Trim();

        return (string.IsNullOrWhiteSpace(value: blobName) ||
            !blobName.StartsWith(value: PrivatePrefix) ||
            blobName.Contains(value: "..")
        )
            ? ("", httpRequestData.CreateResponse(statusCode: HttpStatusCode.BadRequest))
            : (blobName[PrivatePrefix.Length..], null);
    }

    // Move between the tenant's own containers: private/<path> in their home becomes
    // public/<path> in their anchor container (unpublish is the exact inverse). The copy is a
    // native server-side operation (Put Blob From URL) — the storage service reads the source and
    // writes the destination in place; no bytes transit the Function. It runs entirely as the user
    // (the source read is authorized with the user's own storage token), so it honors the same
    // ABAC gates as any other user operation — the CanRead/CanWrite suspend levers and the
    // migration Frozen lever included.
    private async Task MoveAsync(
        BlobContainerClient sourceContainerClient,
        string sourceBlobName,
        BlobContainerClient destinationContainerClient,
        string destinationBlobName,
        CancellationToken cancellationToken
    ) {
        var sourceBlobClient = sourceContainerClient.GetBlobClient(blobName: sourceBlobName);
        var sourceToken = await userCredentialContext
            .UserContext
            .GetTokenAsync(
                cancellationToken: cancellationToken,
                requestContext: new(scopes: ["https://storage.azure.com/.default"])
            );

        await destinationContainerClient
            .GetBlockBlobClient(blobName: destinationBlobName)
            .SyncUploadFromUriAsync(
                cancellationToken: cancellationToken,
                copySource: sourceBlobClient.Uri,
                options: new BlobSyncUploadFromUriOptions {
                    // Carry the source blob's content type and system properties to the copy.
                    CopySourceBlobProperties = true,
                    SourceAuthentication = new(scheme: "Bearer", parameter: sourceToken.Token),
                }
            );
        await sourceBlobClient.DeleteAsync(cancellationToken: cancellationToken);
    }

    [Function(name: nameof(Publish))]
    public async Task<HttpResponseData> Publish(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "post",
            Route = "publish"
        )] HttpRequestData httpRequestData,
        FunctionContext functionContext
    ) {
        var cancellationToken = functionContext.CancellationToken;
        var userObjectId = GetDelegatedUserObjectId(functionContext: functionContext);

        if (userObjectId is null) {
            return httpRequestData.CreateResponse(statusCode: HttpStatusCode.Forbidden);
        }

        var request = await httpRequestData.ReadFromJsonAsync<PublishRequest>(cancellationToken: cancellationToken);
        var (relativePath, error) = ParseBlobName(
            httpRequestData: httpRequestData,
            request: request
        );

        if (error is not null) {
            return error;
        }

        var location = await userStorageLocationService.GetAsync(
            cancellationToken: cancellationToken,
            userObjectId: userObjectId
        );

        // A publish moves a blob OUT of the frozen-and-being-copied home; deferring it beats
        // racing the migration's copy pass and resurrecting the private/ half afterwards.
        if (location.IsMigrating) {
            return await CreateMigratingResponseAsync(
                cancellationToken: cancellationToken,
                httpRequestData: httpRequestData
            );
        }

        await MoveAsync(
            cancellationToken: cancellationToken,
            destinationBlobName: $"{PublicPrefix}{relativePath}",
            destinationContainerClient: GetAnchorContainerClient(userObjectId: userObjectId),
            sourceBlobName: $"{PrivatePrefix}{relativePath}",
            sourceContainerClient: GetHomeContainerClient(
                blobEndpoint: location.BlobEndpoint,
                userObjectId: userObjectId
            )
        );

        var response = httpRequestData.CreateResponse();

        await response.WriteAsJsonAsync(
            cancellationToken: cancellationToken,
            instance: new {
                PublicUrl = $"{GetPublicBaseUrl()}/{userObjectId}/{relativePath}",
            }
        );

        return response;
    }

    [Function(name: nameof(Unpublish))]
    public async Task<HttpResponseData> Unpublish(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "post",
            Route = "unpublish"
        )] HttpRequestData httpRequestData,
        FunctionContext functionContext
    ) {
        var cancellationToken = functionContext.CancellationToken;
        var userObjectId = GetDelegatedUserObjectId(functionContext: functionContext);

        if (userObjectId is null) {
            return httpRequestData.CreateResponse(statusCode: HttpStatusCode.Forbidden);
        }

        var request = await httpRequestData.ReadFromJsonAsync<PublishRequest>(cancellationToken: cancellationToken);
        var (relativePath, error) = ParseBlobName(
            httpRequestData: httpRequestData,
            request: request
        );

        if (error is not null) {
            return error;
        }

        var location = await userStorageLocationService.GetAsync(
            cancellationToken: cancellationToken,
            userObjectId: userObjectId
        );

        // An unpublish writes INTO the frozen home, which the Frozen lever would reject anyway;
        // fail fast with a clear message instead of a bare storage 403.
        if (location.IsMigrating) {
            return await CreateMigratingResponseAsync(
                cancellationToken: cancellationToken,
                httpRequestData: httpRequestData
            );
        }

        await MoveAsync(
            cancellationToken: cancellationToken,
            destinationBlobName: $"{PrivatePrefix}{relativePath}",
            destinationContainerClient: GetHomeContainerClient(
                blobEndpoint: location.BlobEndpoint,
                userObjectId: userObjectId
            ),
            sourceBlobName: $"{PublicPrefix}{relativePath}",
            sourceContainerClient: GetAnchorContainerClient(userObjectId: userObjectId)
        );

        var response = httpRequestData.CreateResponse();

        await response.WriteAsJsonAsync(
            cancellationToken: cancellationToken,
            instance: new { BlobName = $"{PrivatePrefix}{relativePath}", }
        );

        return response;
    }

    [Function(name: nameof(ListPublicFiles))]
    public async Task<HttpResponseData> ListPublicFiles(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "get",
            Route = "public-files"
        )] HttpRequestData httpRequestData,
        FunctionContext functionContext
    ) {
        var cancellationToken = functionContext.CancellationToken;
        var userObjectId = GetDelegatedUserObjectId(functionContext: functionContext);

        if (userObjectId is null) {
            return httpRequestData.CreateResponse(statusCode: HttpStatusCode.Forbidden);
        }

        var prefix = PublicPrefix;
        var publicBaseUrl = GetPublicBaseUrl();
        var files = new List<object>();

        await foreach (var blob in GetAnchorContainerClient(userObjectId: userObjectId).GetBlobsAsync(
            BlobTraits.None,
            BlobStates.None,
            prefix,
            cancellationToken
        )) {
            var relativePath = blob.Name[prefix.Length..];

            files.Add(item: new {
                BlobName = $"{PrivatePrefix}{relativePath}",
                PublicUrl = $"{publicBaseUrl}/{userObjectId}/{relativePath}",
                SizeInBytes = blob.Properties.ContentLength,
            });
        }

        var response = httpRequestData.CreateResponse();

        await response.WriteAsJsonAsync(
            cancellationToken: cancellationToken,
            instance: files
        );

        return response;
    }
}

