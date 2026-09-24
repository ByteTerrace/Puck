using Puck.Storage;

namespace Puck.World.Server;

/// <summary>Read-only root projection used by hosted document consumers that only receive the byte-store seam. The
/// authority root names the published definition; no root means no published definition.</summary>
internal static class WorldAuthorityRootReader {
    public static async ValueTask<ObjectBlobContent?> ReadDefinitionAsync(Guid owner, SafeName world, IObjectBlobStore store, ObjectStorageTarget target, CancellationToken cancellationToken) {
        var identity = new WorldAuthorityIdentity(
            Owner: owner,
            World: world
        );
        var rootAddress = WorldAuthorityBlobStore.RootAddress(identity: identity);
        var rootContent = await store.ReadAsync(
            address: rootAddress,
            cancellationToken: cancellationToken,
            target: target
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (rootContent is not { } rootBlob) { return null; }
        if (!WorldAuthorityRootCodec.TryDecode(
            rootBlob.Content.Span,
            out var root,
            out var reason
        )) {
            throw new InvalidDataException(message: $"'{rootAddress.Key}' is corrupt — {reason}");
        }
        if (root.DefinitionHash is not { Length: > 0 } hash) { return null; }
        var address = WorldAuthorityBlobStore.DefinitionCandidateAddress(
            hash: hash,
            identity: identity
        );
        var content = await store.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (content is not { } found) {
            throw new InvalidDataException(message: $"'{rootAddress.Key}' names missing definition '{address.Key}'");
        }
        var actual = WorldDefinitionFileSource.ComputeContentHash(content: found.Content.Span);

        if (!string.Equals(
            a: actual,
            b: hash,
            comparisonType: StringComparison.Ordinal
        )) { throw new InvalidDataException(message: $"'{address.Key}' hashes to {actual}, not {hash}"); }
        return found;
    }
}
