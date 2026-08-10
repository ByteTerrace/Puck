using System.Text;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>
/// The cloud-backed <see cref="IWorldNeighbourResolver"/> — reads a named neighbour's document as an ordinary blob
/// read, reusing <see cref="WorldOwnedWorldSync"/>'s own address shape (the same <see cref="WorldOwnedWorldSync.WorldsNamespace"/>
/// prefix, quoted rather than duplicated) instead of inventing a second resolution mechanism. A
/// <see cref="WorldReference.Document"/> value must be the canonical file name emitted for a
/// <see cref="WorldSafeName"/>-shaped owned-world id. The resolver parses that id and calls
/// <see cref="WorldOwnedWorldSync.AddressFor"/>, so a reader cannot drift from the writer's encoding or reach an
/// object the owned-world writer could never have produced.
/// </summary>
/// <remarks>
/// Read-only, by design: this resolver never adopts, never tracks a version token, and never writes — it exists only
/// so a validator can read a neighbour's declared data (kits, simulation rate, placements) to prove a border-margin
/// claim, not to sync a catalog. It parses the fetched bytes through <see cref="WorldJsonPayload.TryParse{T}(string,
/// System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}, out T, out string)"/> and <see cref="WorldDefinitionMigrations.Apply"/>
/// only — never <see cref="WorldDefinitionValidator.Validate"/> — because the neighbour's own validity (which may in
/// turn need its own neighbour resolver for a border of its own) is that world's own boot concern, not a proof this
/// resolver re-derives. A read that fails for any reason (not found, no permission, an unreachable endpoint, a
/// malformed document) answers <see cref="WorldNeighbourResolutionKind.Unavailable"/> rather than throwing — the
/// same fail-named discipline <see cref="WorldOwnedWorldSync"/>'s own operations follow.
/// </remarks>
public sealed class WorldStorageNeighbourResolver : IWorldNeighbourResolver {
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(seconds: 15);

    private readonly Guid m_containerId;
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;

    /// <summary>Initializes the resolver.</summary>
    /// <param name="store">The blob store.</param>
    /// <param name="target">The storage target (the per-user cloud endpoint).</param>
    /// <param name="containerId">The per-user container id the identity resolver produced.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="target"/> is <see langword="null"/>.</exception>
    public WorldStorageNeighbourResolver(IObjectBlobStore store, ObjectStorageTarget target, Guid containerId) {
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: target);

        m_containerId = containerId;
        m_store = store;
        m_target = target;
    }

    /// <inheritdoc/>
    public WorldNeighbourResolution Resolve(string document) {
        if (string.IsNullOrWhiteSpace(value: document)) {
            return WorldNeighbourResolution.Unavailable(reason: "the reference names no document");
        }

        if (!document.EndsWith(value: WorldOwnedWorldFileName.Suffix, comparisonType: StringComparison.Ordinal)) {
            return WorldNeighbourResolution.Unavailable(reason: $"document '{document}' is not a canonical owned-world file name ending in '{WorldOwnedWorldFileName.Suffix}'");
        }

        var candidateId = document[..^WorldOwnedWorldFileName.Suffix.Length];

        if (!WorldSafeName.TryParse(candidate: candidateId, name: out var id, reason: out var nameReason) ||
            !string.Equals(a: document, b: WorldOwnedWorldFileName.For(id: id), comparisonType: StringComparison.Ordinal)) {
            return WorldNeighbourResolution.Unavailable(reason: $"document '{document}' is not a canonical owned-world file name — {nameReason}");
        }

        var address = WorldOwnedWorldSync.AddressFor(containerId: m_containerId, id: id);

        ObjectBlobContent? content;

        try {
            using var timeout = new CancellationTokenSource(delay: OperationTimeout);

            content = m_store.ReadAsync(target: m_target, address: address, cancellationToken: timeout.Token).AsTask().GetAwaiter().GetResult();
        } catch (OperationCanceledException) {
            return WorldNeighbourResolution.Unavailable(reason: $"timed out after {OperationTimeout.TotalSeconds:0}s reading '{address.Key}'");
        } catch (Exception exception) {
            return WorldNeighbourResolution.Unavailable(reason: $"transport error reading '{address.Key}' — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }

        if (content is not { } found) {
            return WorldNeighbourResolution.Unavailable(reason: $"no cloud copy at '{address.Key}'");
        }

        string json;

        try {
            json = Encoding.UTF8.GetString(bytes: found.Content.Span);
        } catch (Exception exception) when (exception is ArgumentException or DecoderFallbackException) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{address.Key}' is not valid UTF-8 — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }

        if (!WorldJsonPayload.TryParse(json: json, info: WorldJsonContext.Default.WorldDefinition, value: out var parsed, error: out var parseError)) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{address.Key}' does not parse as {WorldDefinition.SchemaVersion} — {parseError}");
        }

        return WorldNeighbourResolution.Resolved(definition: WorldDefinitionMigrations.Apply(definition: parsed));
    }
}
