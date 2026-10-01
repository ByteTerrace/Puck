using Puck.World.Protocol;

using Puck.Commands;

namespace Puck.World.Server;

/// <summary>A session's observation sink: delivers the observer only what the session's admission tier discloses, only
/// while the session holds <c>observe all</c>, and each snapshot redacted under the world's live observer disclosure.
/// A withheld span ends with the current disclosed definition before the next delivery, so the observer never renders
/// a definition it missed an update to. The answers, compositions and levers this world's hub fans out belong to its
/// own clients and never reach a session. An observer that throws ends its observation; the hub detaches it.</summary>
internal sealed class WorldSessionSink(WorldServer server, WorldSessionObservation observation, IClientSink inner) : IClientSink {
    private bool m_withheld;
    private EntitySnapshot[] m_redacted = [];

    /// <summary>Gets what the observer threw, once it has.</summary>
    public Exception? Fault { get; private set; }

    private bool CanObserve => (
        (Tier != WorldDisclosureTier.Frames) &&
        server.ObservesAsSession(session: observation.Session)
    );
    private WorldDisclosureTier Tier => observation.Tier;

    // Queries are read doors into the same disclosed view as deliveries. Only state observations have their own
    // recipient-filtered projection; the remaining readbacks describe authoritative state that a Presentation or
    // redacted Replica mirror does not carry, so they require the whole replica.
    internal bool AllowsQuery(WorldQuery query) => (
        CanObserve &&
        ((query is WorldQuery.StateObservations) || DisclosesFullReplica)
    );

    private bool DisclosesFullReplica => (
        (Tier == WorldDisclosureTier.Replica) &&
        new WorldSinkDisclosure(
            ObserverBodyIndex: -1,
            Policy: server.Definition.Population.ObserverDisclosure
        ).IsFull
    );

    private bool Discloses() {
        if (CanObserve) {
            return true;
        }

        m_withheld = true;

        return false;
    }
    // Records a delivery fault before it reaches the hub's detach handler. Delivery uses direct calls so a steady
    // snapshot or value-only update does not allocate a capturing delegate.
    private void EndAfterFault(Exception exception) {
        Fault = exception;
        observation.MarkEnded();
        server.GrantTable.MarkSessionFaulted(session: observation.Session);
        server.NoteFaultedSession(session: observation.Session);
    }
    // Ends a withheld span: the observer takes the current disclosed definition before anything newer.
    private void Resume() {
        if (!m_withheld) {
            return;
        }

        m_withheld = false;
        inner.DeliverDefinition(
            definition: Disclose(definition: server.Definition)!,
            version: server.DocumentVersion
        );
    }

    /// <summary>Discloses a definition as this session's tier shows it — see
    /// <see cref="Disclose(WorldDefinition, WorldDisclosureTier, StateArena, Principal?, bool)"/>. Whether the session observes right now is the
    /// delivery's question, not this one's.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The disclosed definition, or <see langword="null"/> at the frames tier.</returns>
    /// <exception cref="InvalidOperationException">The composed projection does not hydrate.</exception>
    public WorldDefinition? Disclose(WorldDefinition definition) => Disclose(
        arena: server.Arena,
        definition: definition,
        recipient: observation.Session,
        tier: Tier,
        unrestricted: false
    );
    /// <summary>Discloses a definition as a tier shows it: verbatim at <see cref="WorldDisclosureTier.Replica"/>, a
    /// projection composed for <paramref name="recipient"/> from <paramref name="arena"/> at
    /// <see cref="WorldDisclosureTier.Presentation"/>, and nothing at <see cref="WorldDisclosureTier.Frames"/>.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="tier">The tier.</param>
    /// <param name="arena">The store the definition's disclosed values and audiences are read from: the live store
    /// for the live definition, the definition's own for a candidate.</param>
    /// <param name="recipient">The recipient the projection is composed for, or <see langword="null"/> for the
    /// public observer.</param>
    /// <param name="unrestricted">Whether to compose as a reader every restriction admits, ignoring
    /// <paramref name="recipient"/>: the most any session could be handed.</param>
    /// <returns>The disclosed definition, or <see langword="null"/> at the frames tier.</returns>
    /// <exception cref="InvalidOperationException">The projection cannot be composed or does not hydrate.</exception>
    public WorldDefinition? Disclose(WorldDefinition definition, WorldDisclosureTier tier, StateArena arena, Principal? recipient, bool unrestricted) {
        if (tier == WorldDisclosureTier.Frames) {
            return null;
        }

        if (tier == WorldDisclosureTier.Replica) {
            return definition;
        }

        var time = server.Time;
        var projection = WorldProjection.Compose(
            arena: arena,
            authority: server.AuthorityIdentity,
            definition: definition,
            recipient: recipient,
            revision: server.Population.Revision,
            tier: tier,
            time: in time,
            unrestricted: unrestricted
        )!;

        return (WorldProjection.TryToDefinition(
            definition: out var disclosed,
            projection: projection,
            reason: out var reason
        )
            ? disclosed!
            : throw new InvalidOperationException(message: $"the projection composed for {observation.Session.Describe()} does not hydrate: {reason}"));
    }
    public void DeliverAnswer(in QueryAnswer answer) {
    }
    public void DeliverComposition(WorldComposition composition) {
    }
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) {
        if (!Discloses()) {
            return;
        }

        try {
            m_withheld = false;
            inner.DeliverDefinition(
                definition: Disclose(definition: definition)!,
                version: version
            );
        } catch (Exception exception) {
            EndAfterFault(exception: exception);

            throw;
        }
    }
    public void DeliverSessionLever(WorldSessionLever lever) {
    }
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        if (!Discloses()) {
            return;
        }

        var redacted = WorldOutputHub.Redact(
            disclosure: new WorldSinkDisclosure(
                ObserverBodyIndex: -1,
                Policy: server.Definition.Population.ObserverDisclosure
            ),
            scratch: ref m_redacted,
            snapshot: in snapshot
        );

        try {
            Resume();
            inner.DeliverSnapshot(snapshot: in redacted);
        } catch (Exception exception) {
            EndAfterFault(exception: exception);

            throw;
        }
    }
    // A projection renumbers the rows it keeps, so a state stamp's row ordinals name the authority's rows, not the
    // projection's: below Replica the observer takes the whole disclosed definition instead. Body redaction does
    // not renumber a Replica's state rows and must not turn value-only updates into structural rebuilds.
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) {
        if (!Discloses()) {
            return;
        }

        try {
            if (
                m_withheld ||
                (Tier != WorldDisclosureTier.Replica)
            ) {
                m_withheld = false;
                inner.DeliverDefinition(
                    definition: Disclose(definition: definition)!,
                    version: version
                );

                return;
            }

            inner.DeliverState(
                definition: definition,
                stamp: in stamp,
                version: version
            );
        } catch (Exception exception) {
            EndAfterFault(exception: exception);

            throw;
        }
    }
}
