using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using Puck.Azure.Functions.Utilities;
using Puck.Storage;

namespace Puck.Azure.Functions.Services;

public sealed record UserStorageLocation(
    int Partition,
    string BlobEndpoint,
    bool IsMigrating
);
/// <summary>
/// Resolves where a user's container actually lives. The authority is the user's grain — its
/// recorded home survives partition-count changes, while the local partitioner only says where a
/// user SHOULD live — so every edge storage operation resolves through here instead of computing
/// locally. Results are cached briefly; correctness does not depend on the TTL, because a stale
/// endpoint's writes are ABAC-frozen during a migration rather than silently lost.
/// </summary>
public interface IUserStorageLocationService {
    Task<UserStorageLocation> GetAsync(
        string userObjectId,
        CancellationToken cancellationToken
    );
}
public sealed class DefaultUserStorageLocationService(
    IConfiguration configuration,
    HybridCache hybridCache,
    IHttpClientFactory httpClientFactory,
    IPartitionResolver partitionResolver
) : IUserStorageLocationService {
    private static readonly HybridCacheEntryOptions CacheEntryOptions = new() {
        Expiration = TimeSpan.FromSeconds(value: 90),
        LocalCacheExpiration = TimeSpan.FromSeconds(value: 90),
    };

    private sealed record ActorStorageLocation(
        int Partition,
        string? BlobEndpoint,
        bool IsMigrating
    );

    public async Task<UserStorageLocation> GetAsync(
        string userObjectId,
        CancellationToken cancellationToken
    ) =>
        await hybridCache.GetOrCreateAsync(
            cancellationToken: cancellationToken,
            factory: async innerCancellationToken => await ResolveAsync(
                cancellationToken: innerCancellationToken,
                userObjectId: userObjectId
            ),
            key: $"storage-location:{userObjectId}",
            options: CacheEntryOptions
        );

    private async Task<UserStorageLocation> ResolveAsync(
        string userObjectId,
        CancellationToken cancellationToken
    ) {
        var actorsBaseUrl = configuration.GetActorsBaseUrl();

        if (actorsBaseUrl is not null) {
            try {
                var location = await httpClientFactory
                    .CreateClient(name: "Actors")
                    .GetFromJsonAsync<ActorStorageLocation>(
                        cancellationToken: cancellationToken,
                        requestUri: $"{actorsBaseUrl}/users/{userObjectId}/storage-location"
                    );

                if (location?.BlobEndpoint is not null) {
                    return new(
                        BlobEndpoint: location.BlobEndpoint,
                        IsMigrating: location.IsMigrating,
                        Partition: location.Partition
                    );
                }
            } catch (HttpRequestException) { } // Silo unreachable: fall through to the local resolver.
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { } // Client timeout.
        }

        var partition = partitionResolver.GetPartition(userObjectId: Guid.Parse(input: userObjectId));

        return new(
            BlobEndpoint: partitionResolver
                .GetBlobEndpoint(partition: partition)
                .ToString(),
            IsMigrating: false,
            Partition: partition
        );
    }
}

