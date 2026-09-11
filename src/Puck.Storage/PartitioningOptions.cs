using System.Globalization;

namespace Puck.Storage;

/// <summary>
/// The storage-account partition address space a user's oid routes into.
/// </summary>
public sealed class PartitioningOptions {
    /// <summary>
    /// The partition that anchors every user's published content. Front Door's only blob origin, so
    /// a user's <c>public/</c> prefix lives in their oid-named container here whatever their home
    /// partition is: published URLs survive a migration and the edge needs no per-partition routing.
    /// </summary>
    public const int AnchorPartition = 0;

    /// <summary>
    /// Gets or sets the number of storage-account partitions (buckets), 1-1024. A user's oid routes to
    /// a bucket via <c>MonotonicPartitioner16x1024</c>; the same router runs in the edge, in the silo,
    /// and (as WASM) in the browser, so every tier agrees on a user's account without coordinating,
    /// and the monotonic invariant means growing the count only migrates users landing in a new bucket.
    /// <c>Count = 1</c> collapses to a single account.
    /// </summary>
    public int Count { get; set; } = 1;
    /// <summary>
    /// Gets or sets the offset added to the bucket index to produce the account index in
    /// <see cref="BlobEndpointFormat" />. Bucket 0 maps to account 1 by default, since
    /// <c>bytrcstp000</c> is the fabric account.
    /// </summary>
    public int AccountIndexOffset { get; set; } = 1;
    /// <summary>
    /// Gets or sets the composite format for a partition's blob endpoint, where <c>{0}</c> is the
    /// account index (bucket + <see cref="AccountIndexOffset" />). A format rather than an explicit map
    /// so the endpoint is derivable for any bucket up to 1024 without enumerating them.
    /// </summary>
    public string BlobEndpointFormat { get; set; } = "https://bytrcstp{0:D3}.blob.core.windows.net";

    /// <summary>
    /// Returns the blob endpoint of the storage account at the given account index.
    /// </summary>
    public Uri BlobEndpointForAccountIndex(int accountIndex) =>
        new(uriString: string.Format(
            provider: CultureInfo.InvariantCulture,
            format: BlobEndpointFormat,
            arg0: accountIndex
        ));
}
