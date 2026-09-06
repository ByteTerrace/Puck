using Azure.Core.Cryptography;

namespace Puck.Azure.Functions;

internal sealed class StaticKeyResolver(IKeyEncryptionKey keyEncryptionKey) : IKeyEncryptionKeyResolver
{
    public IKeyEncryptionKey Resolve(string keyId, CancellationToken cancellationToken = default) =>
        keyEncryptionKey;
    public Task<IKeyEncryptionKey> ResolveAsync(string keyId, CancellationToken cancellationToken = default) =>
        Task.FromResult(result: keyEncryptionKey);
}

