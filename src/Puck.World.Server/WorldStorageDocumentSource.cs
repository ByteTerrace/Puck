using Puck.Storage;

namespace Puck.World.Server;

/// <summary>The storage-backed <see cref="IWorldDocumentSource"/> — resolves a basis-chain member name against the
/// flat cloud <c>puck/worlds/basis/</c> namespace via <see cref="WorldOwnedWorldSync.BasisAddressFor"/>, the storage
/// twin of the directory walk <see cref="WorldDefinitionFileSource"/> runs for a local file. Every document in the
/// namespace addresses every other by its own bare canonical file name — <c>referrerName</c> plays no role in
/// resolution, since there is no directory to be relative to. <see cref="TryRead"/> answers the full blob key as
/// <c>resolvedName</c> (never the bare name alone), so a chain link's identity can never collide with a root seeded
/// from a DIFFERENT namespace under the same bare spelling — see <see cref="WorldOwnedWorldSync.AddressFor"/>'s own
/// callers.</summary>
/// <remarks>One instance's <see cref="CancellationToken"/> bounds an ENTIRE chain walk, not one hop of it —
/// <see cref="WorldDefinitionFileSource.TryComposeChain"/> can call <see cref="TryRead"/> up to
/// <see cref="WorldDocumentBasis.MaxChainDepth"/> times for one root, and a per-hop timeout would let that add up to
/// depth-many multiples of a single operation's budget. Callers construct one instance per compose call, sharing one
/// <see cref="CancellationTokenSource"/> across every hop.</remarks>
public sealed class WorldStorageDocumentSource(IObjectBlobStore store, ObjectStorageTarget target, Guid containerId, CancellationToken cancellationToken) : IWorldDocumentSource {
    // Refuses before any network call: empty/whitespace, leading or trailing whitespace, either slash (which also
    // catches every '.'/'..' traversal, since a segment only means something once a slash is present), or a name
    // that does not end in the owned-world suffix. `a..b.world.json` carries no slash and survives.
    private static bool TryCanonicalize(string name, out string canonical, out string reason) {
        canonical = name;

        if (string.IsNullOrWhiteSpace(value: name)) {
            reason = "the basis reference is empty";

            return false;
        }

        if (!string.Equals(
            a: name,
            b: name.Trim(),
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"'{name}' carries leading or trailing whitespace — the cloud puck/worlds/basis/ namespace addresses each document by its exact canonical name";

            return false;
        }

        if (
            name.Contains(value: '/') ||
            name.Contains(value: '\\')
        ) {
            reason = $"'{name}' is not a bare file name — the cloud puck/worlds/basis/ namespace is flat, no subdirectories or traversal";

            return false;
        }

        if (!name.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldOwnedWorldFileName.Suffix
        )) {
            reason = $"'{name}' does not end in '{WorldOwnedWorldFileName.Suffix}' — every basis document is an owned-world-shaped file";

            return false;
        }

        reason = string.Empty;

        return true;
    }

    /// <inheritdoc/>
    public bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
        resolvedName = name;
        content = null;

        if (!TryCanonicalize(
            canonical: out var canonical,
            name: name,
            reason: out reason
        )) {
            return false;
        }

        var address = WorldOwnedWorldSync.BasisAddressFor(
            containerId: containerId,
            name: canonical
        );

        resolvedName = address.Key;

        ObjectBlobContent? found;

        try {
            found = store.ReadAsync(
                address: address,
                cancellationToken: cancellationToken,
                target: target
            ).AsTask().GetAwaiter().GetResult();
        } catch (OperationCanceledException) {
            reason = $"timed out reading '{address.Key}'";

            return false;
        } catch (Exception exception) {
            reason = $"transport error reading '{address.Key}' — {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (found is not { } blob) {
            reason = $"no cloud copy at '{address.Key}'";

            return false;
        }

        content = blob.Content.ToArray();
        reason = string.Empty;

        return true;
    }
}
