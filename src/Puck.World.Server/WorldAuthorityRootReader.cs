using Puck.Storage;

namespace Puck.World.Server;

/// <summary>Read-only root projection used by hosted document consumers that only receive the byte-store seam. A
/// present root is authoritative; the legacy definition is consulted only before root initialization.</summary>
internal static class WorldAuthorityRootReader {
    private static ObjectBlobAddress PrivateAddress(Guid owner, SafeName world, string leaf) => new(ObjectId: owner, Key: WorldOwnedWorldSync.HostedPrivateNamespace + "/" + world.Value + "/" + leaf);
    private static ObjectBlobAddress RootAddress(Guid owner, SafeName world) => PrivateAddress(owner, world, "authority/root");
    private static ObjectBlobAddress DefinitionAddress(Guid owner, SafeName world, string hash) => PrivateAddress(owner, world, $"authority/definitions/{ExtractHex(hash)}.json");

    public static async ValueTask<ObjectBlobContent?> ReadDefinitionAsync(Guid owner, SafeName world, IObjectBlobStore store, ObjectStorageTarget target, CancellationToken cancellationToken) {
        var rootContent = await store.ReadAsync(target, RootAddress(owner, world), cancellationToken).ConfigureAwait(false);
        if (rootContent is not { } rootBlob) {
            return await store.ReadAsync(target, WorldOwnedWorldSync.HostedAddressFor(owner, world, "definition.json"), cancellationToken).ConfigureAwait(false);
        }
        if (!WorldAuthorityRootCodec.TryDecode(rootBlob.Content.Span, out var root, out var reason)) {
            throw new InvalidDataException($"'{RootAddress(owner, world).Key}' is corrupt — {reason}");
        }
        if (root.DefinitionHash is not { Length: > 0 } hash) { return null; }
        var address = DefinitionAddress(owner, world, hash);
        var content = await store.ReadAsync(target, address, cancellationToken).ConfigureAwait(false);
        if (content is not { } found) { throw new InvalidDataException($"'{RootAddress(owner, world).Key}' names missing definition '{address.Key}'"); }
        var actual = WorldDefinitionFileSource.ComputeContentHash(found.Content.Span);
        if (!string.Equals(actual, hash, StringComparison.Ordinal)) { throw new InvalidDataException($"'{address.Key}' hashes to {actual}, not {hash}"); }
        return found;
    }

    private static string ExtractHex(string hash) {
        const string prefix = "sha256-64/";
        if (!hash.StartsWith(prefix, StringComparison.Ordinal)) { throw new InvalidDataException($"'{hash}' is not a content-address pin"); }
        return hash[prefix.Length..];
    }
}
