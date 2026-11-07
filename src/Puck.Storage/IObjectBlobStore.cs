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
    /// <summary>Lists the keys of every blob under an object beneath <paramref name="keyPrefix"/> — the
    /// cloud-discovery half of the store: a caller that only knows its local and previously-tracked ids uses this to
    /// find objects it has never seen before. Matching is by whole path SEGMENT, not by raw characters: a prefix
    /// <c>worlds</c> matches <c>worlds/a.json</c> and never a sibling <c>worlds2/a.json</c>. Keys come back
    /// object-relative — the same shape <see cref="ObjectBlobAddress.Key"/> carries — so a discovered key addresses a
    /// read directly. Version tokens are deliberately NOT returned: every consumer reads the blob it discovered, and
    /// <see cref="ReadAsync"/> already yields the token with the bytes, so listing one would be a second answer to the
    /// same question with a wider window for it to be stale in. A target whose store cannot list throws by name
    /// rather than returning an empty result the caller cannot tell from an honestly empty prefix.</summary>
    /// <param name="target">The storage target the object resolves against.</param>
    /// <param name="objectId">The object (container) id to list within.</param>
    /// <param name="keyPrefix">The key path to list beneath (relative, no dot segments); empty lists every key under
    /// the object.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>One object-relative key per matching blob, in backend-returned order.</returns>
    ValueTask<IReadOnlyList<string>> ListAsync(
        ObjectStorageTarget target,
        Guid objectId,
        string keyPrefix,
        CancellationToken cancellationToken = default
    );
    /// <summary>Probes whether this host could authenticate to <paramref name="target"/>'s store right now, without
    /// prompting, reading or writing a blob. A target that needs no credential answers available.</summary>
    /// <param name="target">The storage target to probe.</param>
    /// <param name="timeout">How long, on <paramref name="clock"/>, to wait before giving up.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <param name="clock">The host clock <paramref name="timeout"/> runs on; <see langword="null"/> is
    /// <see cref="TimeProvider.System"/>.</param>
    /// <returns>The verdict; a timeout or credential failure is an unavailable verdict, never an exception.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    ValueTask<ObjectStorageAccess> ProbeAccessAsync(ObjectStorageTarget target, TimeSpan timeout, CancellationToken cancellationToken = default,
        TimeProvider? clock = null) {
        ArgumentNullException.ThrowIfNull(target);

        return target.Backend.ProbeAccessAsync(
            cancellationToken: cancellationToken,
            clock: (clock ?? TimeProvider.System),
            target: target,
            timeout: timeout
        );
    }
}
