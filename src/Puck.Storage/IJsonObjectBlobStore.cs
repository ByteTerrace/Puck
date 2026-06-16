namespace Puck.Storage;

public interface IJsonObjectBlobStore {
    ValueTask<ObjectBlobReadResult<T>> ReadAsync<T>(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        CancellationToken cancellationToken = default
    );
    ValueTask<bool> WriteAsync<T>(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        T value,
        ObjectBlobWriteMode mode,
        CancellationToken cancellationToken = default
    );
}
