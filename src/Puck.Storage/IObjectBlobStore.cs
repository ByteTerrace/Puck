namespace Puck.Storage;

/// <summary>The routed byte-level blob store — reads and writes opaque blobs at an <see cref="ObjectBlobAddress"/>
/// against a chosen <see cref="ObjectStorageTarget"/>, carrying a version token for optimistic concurrency (§2.5.2).</summary>
public interface IObjectBlobStore {
    /// <summary>Reads a blob's bytes and version token, or <see langword="null"/> when it does not exist.</summary>
    /// <param name="target">The storage target the address resolves against.</param>
    /// <param name="address">The blob address.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The blob content and its token, or <see langword="null"/> when absent.</returns>
    ValueTask<ObjectBlobContent?> ReadAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        CancellationToken cancellationToken = default
    );

    /// <summary>Writes a blob, optionally guarded by an if-match version token, returning whether it landed, whether a
    /// precondition refused it, and the new token.</summary>
    /// <param name="target">The storage target the address resolves against.</param>
    /// <param name="address">The blob address.</param>
    /// <param name="content">The bytes to write.</param>
    /// <param name="mode">Overwrite or create-only.</param>
    /// <param name="ifMatchVersion">The version token the current blob must still carry, or <see langword="null"/> for
    /// an unconditional write. A mismatch refuses the write with <see cref="ObjectBlobWriteResult.PreconditionFailed"/>.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The write outcome and the new token.</returns>
    ValueTask<ObjectBlobWriteResult> WriteAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        ReadOnlyMemory<byte> content,
        ObjectBlobWriteMode mode,
        string? ifMatchVersion = null,
        CancellationToken cancellationToken = default
    );
}
