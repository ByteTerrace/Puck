namespace Puck.Storage;

internal interface IObjectBlobStoreBackend {
    ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        CancellationToken cancellationToken = default
    );
    bool Supports(ObjectStorageTarget target);
    ValueTask<bool> WriteAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        ReadOnlyMemory<byte> content,
        ObjectBlobWriteMode mode,
        CancellationToken cancellationToken = default
    );
}
