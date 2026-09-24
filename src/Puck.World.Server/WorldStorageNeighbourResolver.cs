using Puck.Networking;
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

    /// <summary>The hosted-world authority roots (<see cref="WorldAuthorityBlobStore"/>) — a neighbour's published
    /// definition is always stored already composed.</summary>
    Hosted,
}
/// <summary>
/// The cloud-backed <see cref="IWorldNeighbourResolver"/> — reads a named neighbour's document as an ordinary blob
/// read, reusing <see cref="WorldOwnedWorldSync"/>'s own address shape (the same namespace prefix, quoted rather than
/// duplicated) instead of inventing a second resolution mechanism. A <see cref="WorldReference.Document"/> value must
/// be the canonical file name emitted for a <see cref="SafeName"/>-shaped world id. The resolver parses that id
/// and reads <see cref="WorldOwnedWorldSync.AddressFor"/> or the definition the id's authority root names
/// (selected by <see cref="WorldStorageNamespace"/>), so a reader cannot drift from the writer's encoding or reach an
/// object the writer could never have produced.
/// </summary>
/// <remarks>
/// Read-only, by design: this resolver never adopts, never tracks a version token, and never writes — it exists only
/// so a validator can read a neighbour's declared data (kits, simulation rate, placements) to prove an adjacency
/// claim, not to sync a catalog. It parses the fetched bytes through <see cref="WorldJsonPayload.TryParse{T}(string,
/// System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}, out T, out string, bool)"/> only — never <see cref="WorldDefinitionValidator.Validate"/> — because the neighbour's own validity (which may in
/// turn need its own neighbour resolver for a border of its own) is that world's own boot concern, not a proof this
/// resolver re-derives. A read that fails for any reason (not found, no permission, an unreachable endpoint, a
/// malformed document) answers <see cref="WorldNeighbourResolutionKind.Unavailable"/> rather than throwing — the
/// same fail-named discipline <see cref="WorldOwnedWorldSync"/>'s own operations follow.
/// </remarks>
public sealed class WorldStorageNeighbourResolver : IWorldNeighbourResolver {
    /// <summary>Gets the bound on each storage read, and separately on the whole basis chain, on the host clock.</summary>
    public static TimeSpan OperationTimeout { get; } = TimeSpan.FromSeconds(seconds: 15);

    private readonly TimeProvider m_clock;
    private readonly Guid m_containerId;
    private readonly WorldStorageNamespace m_namespace;
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;

    /// <summary>Initializes the resolver.</summary>
    /// <param name="store">The blob store.</param>
    /// <param name="target">The storage target (the per-user cloud endpoint).</param>
    /// <param name="containerId">The per-user container id the identity resolver produced.</param>
    /// <param name="namespace">Which of the two blob namespaces to address a resolved neighbour under.</param>
    /// <param name="timeProvider">The host clock each read's bound runs on; <see langword="null"/> is
    /// <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="target"/> is <see langword="null"/>.</exception>
    public WorldStorageNeighbourResolver(IObjectBlobStore store, ObjectStorageTarget target, Guid containerId, WorldStorageNamespace @namespace = WorldStorageNamespace.Worlds, TimeProvider? timeProvider = null) {
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: target);

        m_clock = (timeProvider ?? TimeProvider.System);
        m_containerId = containerId;
        m_namespace = @namespace;
        m_store = store;
        m_target = target;
    }

    // Hosted definitions are published fully composed; boot must never turn a basis into blocking storage reads.
    internal async ValueTask<WorldNeighbourResolution> ResolveHostedAsync(string document, CancellationToken cancellationToken) {
        if (!TryWorldId(
            document: document,
            id: out var id,
            reason: out var reason
        )) { return WorldNeighbourResolution.Unavailable(reason: reason); }
        var address = WorldAuthorityBlobStore.RootAddress(identity: new(
            Owner: m_containerId,
            World: id
        ));
        using var timeout = new OperationDeadline(
            caller: cancellationToken,
            timeout: OperationTimeout,
            timeProvider: m_clock
        );

        try {
            var content = await WorldAuthorityRootReader.ReadDefinitionAsync(
                m_containerId,
                id,
                m_store,
                m_target,
                timeout.Token
            ).ConfigureAwait(continueOnCapturedContext: false);

            cancellationToken.ThrowIfCancellationRequested();
            if (content is not { } found) { return WorldNeighbourResolution.Unavailable(reason: $"no published definition at '{address.Key}'"); }
            return ParseAttestation(
                Encoding.UTF8.GetString(bytes: found.Content.Span),
                address.Key,
                document
            );
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; } catch (Exception error) { return WorldNeighbourResolution.Unavailable(reason: $"could not read '{address.Key}' — {error.Message.ReplaceLineEndings(replacementText: " ")}"); }
    }

    private static WorldNeighbourResolution ParseAttestation(string json, string sourceName, string document) {
        // Bind creation expressions, but prove only the seam facts needed by this world.
        if (!WorldDefinitionFileSource.TryParseDocument(
            definition: out var parsed,
            json: json,
            reason: out var parseError,
            sourceName: sourceName
        )) {
            return WorldNeighbourResolution.Unavailable(reason: $"'{sourceName}' does not parse as {WorldDefinition.SchemaVersion} — {parseError}");
        }

        return ((WorldCounterpartAttestation.TryCompose(
            attestation: out var attestation,
            definition: parsed!,
            document: document,
            reason: out var attestReason
        ) && (attestation is not null))
            ? WorldNeighbourResolution.Attested(attestation: attestation)
            : WorldNeighbourResolution.Unavailable(reason: $"'{sourceName}' declares no attestable seam — {attestReason}")
        );
    }
    private static bool TryWorldId(string document, out SafeName id, out string reason) => WorldDocumentName.TryParseId(
        id: out id,
        name: document,
        reason: out reason
    );

    /// <inheritdoc/>
    public WorldNeighbourResolution Resolve(string document) {
        if (m_namespace == WorldStorageNamespace.Hosted) {
            return ResolveHostedAsync(
            document,
            CancellationToken.None
        ).AsTask().GetAwaiter().GetResult();
        }
        if (!TryWorldId(
            document: document,
            id: out var id,
            reason: out var reason
        )) { return WorldNeighbourResolution.Unavailable(reason: reason); }
        var address = WorldOwnedWorldSync.AddressFor(
            containerId: m_containerId,
            id: id
        );

        ObjectBlobContent? content;

        try {
            using var timeout = new CancellationTokenSource(
                delay: OperationTimeout,
                timeProvider: m_clock
            );

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
            using var chainTimeout = new CancellationTokenSource(
                delay: OperationTimeout,
                timeProvider: m_clock
            );

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

        return ParseAttestation(
            json,
            address.Key,
            document
        );
    }
}
