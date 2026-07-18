namespace Puck.Storage;

/// <summary>The JSON mirror of <see cref="IObjectBlobStore"/> — (de)serializes a document through the byte store,
/// carrying the version token so an editor can round-trip a document with optimistic concurrency (§2.5.2).</summary>
public interface IJsonObjectBlobStore {
    /// <summary>Reads and deserializes a document, returning whether it was found, the value, and its version token.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="target">The storage target.</param>
    /// <param name="address">The blob address.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The typed read result.</returns>
    ValueTask<ObjectBlobReadResult<T>> ReadAsync<T>(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        CancellationToken cancellationToken = default
    );

    /// <summary>Serializes and writes a document, optionally guarded by an if-match version token.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="target">The storage target.</param>
    /// <param name="address">The blob address.</param>
    /// <param name="value">The document to persist.</param>
    /// <param name="mode">Overwrite or create-only.</param>
    /// <param name="ifMatchVersion">The version token the stored blob must still carry, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>The write outcome and the new token.</returns>
    ValueTask<ObjectBlobWriteResult> WriteAsync<T>(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        T value,
        ObjectBlobWriteMode mode,
        string? ifMatchVersion = null,
        CancellationToken cancellationToken = default
    );
}
