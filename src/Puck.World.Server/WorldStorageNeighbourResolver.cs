using System.Text;
using System.Text.Json.Nodes;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>Which of <see cref="WorldOwnedWorldSync"/>'s two blob namespaces a <see cref="WorldStorageNeighbourResolver"/>
/// addresses a resolved neighbour under.</summary>
public enum WorldStorageNamespace {
    /// <summary>The owned-worlds catalog namespace (<see cref="WorldOwnedWorldSync.AddressFor"/>) — a neighbour may
    /// carry a basis chain.</summary>
    Worlds,

    /// <summary>The hosted-world namespace (<see cref="WorldOwnedWorldSync.HostedAddressFor"/>) — a neighbour is
    /// always stored already composed.</summary>
    Hosted,
}
/// <summary>
/// The cloud-backed <see cref="IWorldNeighbourResolver"/> — reads a named neighbour's document as an ordinary blob
/// read, reusing <see cref="WorldOwnedWorldSync"/>'s own address shape (the same namespace prefix, quoted rather than
/// duplicated) instead of inventing a second resolution mechanism. A <see cref="WorldReference.Document"/> value must
/// be the canonical file name emitted for a <see cref="SafeName"/>-shaped world id. The resolver parses that id
/// and calls <see cref="WorldOwnedWorldSync.AddressFor"/> or <see cref="WorldOwnedWorldSync.HostedAddressFor"/>
/// (selected by <see cref="WorldStorageNamespace"/>), so a reader cannot drift from the writer's encoding or reach an
/// object the writer could never have produced.
/// </summary>
/// <remarks>
/// Read-only, by design: this resolver never adopts, never tracks a version token, and never writes — it exists only
/// so a validator can read a neighbour's declared data (kits, simulation rate, placements) to prove an adjacency
/// claim, not to sync a catalog. It parses the fetched bytes through <see cref="WorldJsonPayload.TryParse{T}(string,
/// System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}, out T, out string, bool)"/> and <see cref="WorldDefinitionMigrations.Apply"/>
/// only — never <see cref="WorldDefinitionValidator.Validate"/> — because the neighbour's own validity (which may in
/// turn need its own neighbour resolver for a border of its own) is that world's own boot concern, not a proof this
/// resolver re-derives. A read that fails for any reason (not found, no permission, an unreachable endpoint, a
/// malformed document) answers <see cref="WorldNeighbourResolutionKind.Unavailable"/> rather than throwing — the
/// same fail-named discipline <see cref="WorldOwnedWorldSync"/>'s own operations follow.
/// </remarks>
public sealed class WorldStorageNeighbourResolver : IWorldNeighbourResolver {
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(seconds: 15);

    private readonly Guid m_containerId;
    private readonly WorldStorageNamespace m_namespace;
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;

    /// <summary>Initializes the resolver.</summary>
    /// <param name="store">The blob store.</param>
    /// <param name="target">The storage target (the per-user cloud endpoint).</param>
    /// <param name="containerId">The per-user container id the identity resolver produced.</param>
    /// <param name="namespace">Which of the two blob namespaces to address a resolved neighbour under.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="target"/> is <see langword="null"/>.</exception>
    public WorldStorageNeighbourResolver(IObjectBlobStore store, ObjectStorageTarget target, Guid containerId, WorldStorageNamespace @namespace = WorldStorageNamespace.Worlds) {
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: target);

        m_containerId = containerId;
        m_namespace = @namespace;
        m_store = store;
        m_target = target;
    }

    /// <inheritdoc/>
    public WorldNeighbourResolution Resolve(string document) {
        if (string.IsNullOrWhiteSpace(value: document)) {
            return WorldNeighbourResolution.Unavailable(reason: "the reference names no document");
        }

        if (!document.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldOwnedWorldFileName.Suffix
        )) {
            return WorldNeighbourResolution.Unavailable(reason: $"document '{document}' is not a canonical owned-world file name ending in '{WorldOwnedWorldFileName.Suffix}'");
        }

        var candidateId = document[..^WorldOwnedWorldFileName.Suffix.Length];

        if (
            !SafeName.TryParse(
            candidate: candidateId,
            name: out var id,
            reason: out var nameReason
        ) ||
            !string.Equals(
            a: document,
            b: WorldOwnedWorldFileName.For(id: id),
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return WorldNeighbourResolution.Unavailable(reason: $"document '{document}' is not a canonical owned-world file name — {nameReason}");
        }

        var address = ((m_namespace == WorldStorageNamespace.Hosted)
            ? WorldOwnedWorldSync.HostedAddressFor(
                containerId: m_containerId,
                leaf: "definition.json",
                world: id
            )
            : WorldOwnedWorldSync.AddressFor(
                containerId: m_containerId,
                id: id
            )
        );

        ObjectBlobContent? content;

        try {
            using var timeout = new CancellationTokenSource(delay: OperationTimeout);

            content = m_store.ReadAsync(
                target: m_target,
                address: address,
                cancellationToken: timeout.Token
            ).AsTask().GetAwaiter().GetResult();
        } catch (OperationCanceledException) {
            return WorldNeighbourResolution.Unavailable(reason: $"timed out after {OperationTimeout.TotalSeconds:0}s reading '{address.Key}'");
        } catch (Exception exception) {
            return WorldNeighbourResolution.Unavailable(reason: $"transport error reading '{address.Key}' — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }

        if (content is not { } found) {
            return WorldNeighbourResolution.Unavailable(reason: $"no cloud copy at '{address.Key}'");
        }

        JsonObject? composed;
        string composeReason;

        try {
            using var chainTimeout = new CancellationTokenSource(delay: OperationTimeout);

            if (!WorldDefinitionFileSource.TryComposeChain(
                source: new WorldStorageDocumentSource(
                    cancellationToken: chainTimeout.Token,
                    containerId: m_containerId,
                    store: m_store,
                    target: m_target
                ),
                // Seeded from the root's own blob key (matching WorldOwnedWorldSync.PullOne), so a basis link
                // sharing the root's bare document name can never read as a cycle back to it.
                rootResolvedName: address.Key,
                rootBytes: found.Content.ToArray(),
                composed: out composed,
                chainBytes: out _,
                reason: out composeReason
            )) {
                return WorldNeighbourResolution.Unavailable(reason: $"'{address.Key}' basis chain refused: {composeReason}");
            }
        } catch (OperationCanceledException) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{address.Key}' basis chain compose timed out after {OperationTimeout.TotalSeconds:0}s");
        }

        string json;

        if (composed is not null) {
            json = composed.ToJsonString();
        } else {
            try {
                json = Encoding.UTF8.GetString(bytes: found.Content.Span);
            } catch (Exception exception) when ((exception is ArgumentException or DecoderFallbackException)) {
                return WorldNeighbourResolution.Unavailable(reason: $"'{address.Key}' is not valid UTF-8 — {exception.Message.ReplaceLineEndings(replacementText: " ")}");
            }
        }

        if (!WorldJsonPayload.TryParse(
            json: json,
            info: WorldJsonContext.Default.WorldDefinition,
            value: out var parsed,
            error: out var parseError
        )) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{address.Key}' does not parse as {WorldDefinition.SchemaVersion} — {parseError}");
        }

        // The neighbour's document is reduced to its seam facts here and never handed to the validator: a cloud copy
        // is fetched to prove a border, not to read a world.
        return ((WorldCounterpartAttestation.TryCompose(
            definition: WorldDefinitionMigrations.Apply(definition: parsed),
            document: document,
            attestation: out var attestation,
            reason: out var attestReason
        ) && (attestation is not null))
            ? WorldNeighbourResolution.Attested(attestation: attestation)
            : WorldNeighbourResolution.Unavailable(reason: $"'{address.Key}' declares no attestable seam — {attestReason}")
        );
    }
}
