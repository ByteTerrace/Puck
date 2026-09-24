using Puck.Storage;

namespace Puck.World.Server;

/// <summary>The storage-backed <see cref="IWorldDocumentSource"/> — resolves a basis-chain member name against the
/// flat cloud <c>puck/worlds/basis/</c> namespace via <see cref="WorldOwnedWorldSync.BasisAddressFor"/>, the storage
/// twin of the directory walk <see cref="WorldDefinitionFileSource"/> runs for a local file. Every document in the
/// namespace addresses every other by its bare document name, an owned world id — <c>referrerName</c> plays no role in
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
    /// <inheritdoc/>
    public bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
        resolvedName = name;
        content = null;

        // Refuses before any network call: a file-form spelling, whitespace, and any slash (so every traversal).
        if (!WorldDocumentName.TryParseId(
            id: out var id,
            name: name,
            reason: out reason
        )) {
            reason = $"{reason} — the cloud puck/worlds/basis/ namespace is flat and addresses each document by its owned world id";

            return false;
        }

        var address = WorldOwnedWorldSync.BasisAddressFor(
            containerId: containerId,
            id: id
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
