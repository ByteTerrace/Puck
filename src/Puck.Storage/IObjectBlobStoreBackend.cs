namespace Puck.Storage;

/// <summary>
/// One storage provider's blob I/O over its own <see cref="ObjectStorageTarget"/> type. A provider implements it beside
/// its target and returns it from <see cref="ObjectStorageTarget.Backend"/>; callers reach it only through the routed
/// <see cref="IObjectBlobStore"/>. The semantics every backend keeps are <see cref="IObjectBlobStore"/>'s.
/// </summary>
public interface IObjectBlobStoreBackend {
    /// <summary>See <see cref="IObjectBlobStore.ReadAsync"/>.</summary>
    /// <param name="target">The backend's own target.</param>
    /// <param name="address">The blob address.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The blob content and its token, or <see langword="null"/> when absent.</returns>
    ValueTask<ObjectBlobContent?> ReadAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        CancellationToken cancellationToken = default
    );
    /// <summary>See <see cref="IObjectBlobStore.WriteAsync"/>.</summary>
    /// <param name="target">The backend's own target.</param>
    /// <param name="address">The blob address.</param>
    /// <param name="content">The bytes to write.</param>
    /// <param name="mode">Overwrite or create-only.</param>
    /// <param name="ifMatchVersion">The version token the current blob must still carry, or <see langword="null"/>.</param>
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
    /// <summary>See <see cref="IObjectBlobStore.ListAsync"/>.</summary>
    /// <param name="target">The backend's own target.</param>
    /// <param name="objectId">The object (container) id to list within.</param>
    /// <param name="keyPrefix">The key path to list beneath.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>One object-relative key per matching blob.</returns>
    ValueTask<IReadOnlyList<string>> ListAsync(
        ObjectStorageTarget target,
        Guid objectId,
        string keyPrefix,
        CancellationToken cancellationToken = default
    );
    /// <summary>See <see cref="IObjectBlobStore.ProbeAccessAsync"/>.</summary>
    /// <param name="target">The backend's own target.</param>
    /// <param name="timeout">How long, on <paramref name="clock"/>, to wait before giving up.</param>
    /// <param name="clock">The host clock the timeout runs on.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The verdict.</returns>
    ValueTask<ObjectStorageAccess> ProbeAccessAsync(
        ObjectStorageTarget target,
        TimeSpan timeout,
        TimeProvider clock,
        CancellationToken cancellationToken = default
    );
}
/// <summary>The verdict of an access probe: whether this host could authenticate to a target's store right now, when that
/// access expires, and one line of detail a console verb can echo.</summary>
/// <param name="Available">Whether access could be established.</param>
/// <param name="Detail">What answered, or the first line of why nothing could.</param>
/// <param name="ExpiresOn">When the established access expires, or <see langword="null"/> when it does not or none was
/// established.</param>
public readonly record struct ObjectStorageAccess(bool Available, string Detail, DateTimeOffset? ExpiresOn);
