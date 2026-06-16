namespace Puck.Storage;

internal sealed class ObjectBlobStore(IEnumerable<IObjectBlobStoreBackend> backends) : IObjectBlobStore {
    private readonly IObjectBlobStoreBackend[] m_backends = [.. backends];

    private IObjectBlobStoreBackend ResolveBackend(ObjectStorageTarget target, ObjectBlobAddress address) {
        foreach (var backend in m_backends) {
            if (backend.Supports(target: target)) {
                return backend;
            }
        }

        throw new InvalidOperationException(message: $"No object blob store backend is registered for target type '{target.GetType().Name}' (objectId: {address.ObjectId}).");
    }

    public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(target);

        return ResolveBackend(
            address: address,
            target: target
        ).ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        );
    }
    public ValueTask<bool> WriteAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        ReadOnlyMemory<byte> content,
        ObjectBlobWriteMode mode,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(target);

        return ResolveBackend(
            address: address,
            target: target
        ).WriteAsync(
            address: address,
            cancellationToken: cancellationToken,
            content: content,
            mode: mode,
            target: target
        );
    }
}
