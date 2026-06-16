namespace Puck.Storage;

public interface IObjectBlobStore {
    ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        CancellationToken cancellationToken = default
    );
    ValueTask<bool> WriteAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        ReadOnlyMemory<byte> content,
        ObjectBlobWriteMode mode,
        CancellationToken cancellationToken = default
    );
}
