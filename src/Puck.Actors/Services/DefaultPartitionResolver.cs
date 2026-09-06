using Microsoft.Extensions.Options;
using Puck.Maths;

namespace Puck.Actors.Services;

/// <summary>
/// Resolves a user's oid to the storage-account partition that owns their container — the silo-side
/// counterpart of the edge's resolver and the browser's WASM router, all running the same
/// deterministic partitioner so they never have to agree out of band.
/// </summary>
public interface IPartitionResolver
{
    int PartitionCount { get; }
    int GetPartition(Guid userObjectId);
    Uri GetBlobEndpoint(Guid userObjectId);
    Uri GetBlobEndpoint(int partition);
}

public sealed class DefaultPartitionResolver(
    IOptionsMonitor<PartitioningOptions> partitioningOptions
) : IPartitionResolver
{
    public int PartitionCount => partitioningOptions.CurrentValue.Count;

    public int GetPartition(Guid userObjectId) =>
        MonotonicPartitioner.GetBucketId(
            bucketCount: partitioningOptions.CurrentValue.Count,
            value: userObjectId
        );

    public Uri GetBlobEndpoint(Guid userObjectId) =>
        GetBlobEndpoint(partition: GetPartition(userObjectId: userObjectId));

    public Uri GetBlobEndpoint(int partition) {
        var options = partitioningOptions.CurrentValue;

        return options.BlobEndpointForAccountIndex(accountIndex: (partition + options.AccountIndexOffset));
    }
}

