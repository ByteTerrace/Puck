using Microsoft.Extensions.Options;
using Puck.Maths;

namespace Puck.Storage;

/// <summary>
/// Resolves a user's oid to the storage-account partition that owns their container. The same
/// deterministic router runs at the edge, in the silo, and (as WASM) in the browser, so every tier
/// agrees on a user's partition without coordinating.
/// </summary>
public interface IPartitionResolver {
    /// <summary>
    /// Gets the number of partitions currently in the address space.
    /// </summary>
    int PartitionCount { get; }

    /// <summary>
    /// Returns the partition that owns the given user's container.
    /// </summary>
    int GetPartition(Guid userObjectId);
    /// <summary>
    /// Returns the blob endpoint of the partition that owns the given user's container.
    /// </summary>
    Uri GetBlobEndpoint(Guid userObjectId);
    /// <summary>
    /// Returns the blob endpoint of the given partition.
    /// </summary>
    Uri GetBlobEndpoint(int partition);
}
/// <summary>
/// The <see cref="MonotonicPartitioner" />-backed resolver over <see cref="PartitioningOptions" />.
/// </summary>
public sealed class DefaultPartitionResolver(
    IOptionsMonitor<PartitioningOptions> partitioningOptions
) : IPartitionResolver {
    /// <inheritdoc />
    public int PartitionCount => partitioningOptions.CurrentValue.Count;

    /// <inheritdoc />
    public int GetPartition(Guid userObjectId) =>
        MonotonicPartitioner.GetBucketId(
            bucketCount: partitioningOptions.CurrentValue.Count,
            value: userObjectId
        );
    /// <inheritdoc />
    public Uri GetBlobEndpoint(Guid userObjectId) =>
        GetBlobEndpoint(partition: GetPartition(userObjectId: userObjectId));
    /// <inheritdoc />
    public Uri GetBlobEndpoint(int partition) {
        var options = partitioningOptions.CurrentValue;

        return options.BlobEndpointForAccountIndex(accountIndex: (partition + options.AccountIndexOffset));
    }
}
