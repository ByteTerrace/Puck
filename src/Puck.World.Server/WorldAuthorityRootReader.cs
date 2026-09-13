using Puck.Storage;

namespace Puck.World.Server;

/// <summary>Read-only root projection used by hosted document consumers that only receive the byte-store seam. A
/// present root is authoritative; the legacy definition is consulted only before root initialization.</summary>
internal static class WorldAuthorityRootReader {
    private static ObjectBlobAddress DefinitionAddress(Guid owner, SafeName world, string hash) => PrivateAddress(
        owner,
        world,
        $"authority/definitions/{ExtractHex(hash: hash)}.json"
    );
    private static string ExtractHex(string hash) {
        const string Prefix = "sha256-64/";

        if (!hash.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: Prefix
        )) { throw new InvalidDataException(message: $"'{hash}' is not a content-address pin"); }
        return hash[Prefix.Length..];
    }
    private static ObjectBlobAddress PrivateAddress(Guid owner, SafeName world, string leaf) => new(
        ObjectId: owner,
        Key: ((((WorldOwnedWorldSync.HostedPrivateNamespace + "/") + world.Value) + "/") + leaf)
    );
    private static ObjectBlobAddress RootAddress(Guid owner, SafeName world) => PrivateAddress(
        leaf: "authority/root",
        owner: owner,
        world: world
    );

    public static async ValueTask<ObjectBlobContent?> ReadDefinitionAsync(Guid owner, SafeName world, IObjectBlobStore store, ObjectStorageTarget target, CancellationToken cancellationToken) {
        var rootContent = await store.ReadAsync(
            target,
            RootAddress(
                owner: owner,
                world: world
            ),
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (rootContent is not { } rootBlob) {
            return await store.ReadAsync(
                target,
                WorldOwnedWorldSync.HostedAddressFor(
                    containerId: owner,
                    leaf: "definition.json",
                    world: world
                ),
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        if (!WorldAuthorityRootCodec.TryDecode(
            rootBlob.Content.Span,
            out var root,
            out var reason
        )) {
            throw new InvalidDataException(message: $"'{RootAddress(
                owner: owner,
                world: world
            ).Key}' is corrupt — {reason}");
        }
        if (root.DefinitionHash is not { Length: > 0 } hash) { return null; }
        var address = DefinitionAddress(
            hash: hash,
            owner: owner,
            world: world
        );
        var content = await store.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (content is not { } found) { throw new InvalidDataException(message: $"'{RootAddress(
            owner: owner,
            world: world
        ).Key}' names missing definition '{address.Key}'"); }
        var actual = WorldDefinitionFileSource.ComputeContentHash(content: found.Content.Span);

        if (!string.Equals(
            a: actual,
            b: hash,
            comparisonType: StringComparison.Ordinal
        )) { throw new InvalidDataException(message: $"'{address.Key}' hashes to {actual}, not {hash}"); }
        return found;
    }
}
