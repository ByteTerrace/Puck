using System.Text.Json;

namespace Puck.Storage;

internal sealed class JsonObjectBlobStore(IObjectBlobStore blobStore) : IJsonObjectBlobStore {
    private static readonly JsonSerializerOptions SerializerOptions = new(defaults: JsonSerializerDefaults.Web);
    private readonly IObjectBlobStore m_blobStore = (blobStore ?? throw new ArgumentNullException(paramName: nameof(blobStore)));

    public async ValueTask<ObjectBlobReadResult<T>> ReadAsync<T>(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        CancellationToken cancellationToken = default
    ) {
        var content = await m_blobStore.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        );

        if (content is null) {
            return new ObjectBlobReadResult<T>(
                Found: false,
                Value: default
            );
        }

        var value = JsonSerializer.Deserialize<T>(
            options: SerializerOptions,
            utf8Json: content.Value.Span
        );

        return new ObjectBlobReadResult<T>(
            Found: true,
            Value: value
        );
    }
    public ValueTask<bool> WriteAsync<T>(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        T value,
        ObjectBlobWriteMode mode,
        CancellationToken cancellationToken = default
    ) {
        var content = JsonSerializer.SerializeToUtf8Bytes(
            options: SerializerOptions,
            value: value
        );

        return m_blobStore.WriteAsync(
            address: address,
            cancellationToken: cancellationToken,
            content: content,
            mode: mode,
            target: target
        );
    }
}
