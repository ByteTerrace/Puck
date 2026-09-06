using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Net;
using Puck.Azure.Functions.Services;

namespace Puck.Azure.Functions.HttpTriggers;

// The browser needs to know which partition account holds its container before it can talk to
// storage directly. The authority is the user's grain, not the partitioner: while a migration is
// pending the recorded home and the computed partition differ, and the data is still in the home.
// IUserStorageLocationService wraps the grain call with a short-TTL cache and a local-resolver
// fallback for when the silo is unreachable.
public sealed class StorageEndpoint(IUserStorageLocationService userStorageLocationService)
{
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

    [Function(name: nameof(StorageEndpoint))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "get",
            Route = "storage-endpoint"
        )] HttpRequestData httpRequestData,
        FunctionContext functionContext
    ) {
        var cancellationToken = functionContext.CancellationToken;
        var userObjectId = GetDelegatedUserObjectId(functionContext: functionContext);

        if (userObjectId is null) {
            return httpRequestData.CreateResponse(statusCode: HttpStatusCode.Forbidden);
        }

        var location = await userStorageLocationService.GetAsync(
            cancellationToken: cancellationToken,
            userObjectId: userObjectId
        );
        var response = httpRequestData.CreateResponse();

        await response.WriteAsJsonAsync(
            cancellationToken: cancellationToken,
            instance: new {
                Endpoint = location.BlobEndpoint,
                location.IsMigrating,
            }
        );

        return response;
    }
}

