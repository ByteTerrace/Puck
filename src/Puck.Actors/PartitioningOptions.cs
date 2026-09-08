using System.Globalization;

namespace Puck.Actors;

public sealed class PartitioningOptions
{
    /// <summary>
    /// Number of storage-account partitions (buckets), 1-1024. A user's oid routes to a bucket via
    /// <c>MonotonicPartitioner16x1024</c>; the same router runs in the edge and (as WASM) in the
    /// browser, so every tier agrees on a user's account without coordinating. <c>Count = 1</c>
    /// collapses to a single account and reproduces the pre-partition behaviour exactly.
    /// </summary>
    public int Count { get; set; } = 1;

    /// <summary>
    /// Added to the bucket index to produce the account index in <see cref="BlobEndpointFormat"/>.
    /// Bucket 0 → account 1 by default, since <c>bytrcstp000</c> is the fabric account.
    /// </summary>
    public int AccountIndexOffset { get; set; } = 1;

    /// <summary>
    /// Composite format for a partition's blob endpoint; <c>{0}</c> is the account index
    /// (bucket + <see cref="AccountIndexOffset"/>).
    /// </summary>
    public string BlobEndpointFormat { get; set; } = "https://bytrcstp{0:D3}.blob.core.windows.net";

    public Uri BlobEndpointForAccountIndex(int accountIndex) =>
        new(uriString: string.Format(
            provider: CultureInfo.InvariantCulture,
            format: BlobEndpointFormat,
            arg0: accountIndex
        ));
}

