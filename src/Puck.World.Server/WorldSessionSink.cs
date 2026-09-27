using Puck.World.Protocol;

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

    private WorldDisclosureTier Tier => observation.Tier;

    private bool Discloses() {
        if (
            (Tier != WorldDisclosureTier.Frames) &&
            server.ObservesAsSession(session: observation.Session)
        ) {
            return true;
        }

        m_withheld = true;

        return false;
    }
    // Runs one delivery to the observer, recording a fault and ending the observation before the hub detaches it.
    private void Forward(Action deliver) {
        try {
            deliver();
        } catch (Exception exception) {
            Fault = exception;
            observation.MarkEnded();

            throw;
        }
    }
    // Ends a withheld span: the observer takes the current disclosed definition before anything newer.
    private void Resume() {
        if (!m_withheld) {
            return;
        }

        m_withheld = false;
        inner.DeliverDefinition(definition: Disclose(definition: server.Definition)!);
    }

    /// <summary>Discloses a definition as this session's tier shows it — see
    /// <see cref="Disclose(WorldDefinition, WorldDisclosureTier)"/>. Whether the session observes right now is the
    /// delivery's question, not this one's.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The disclosed definition, or <see langword="null"/> at the frames tier.</returns>
    /// <exception cref="InvalidOperationException">The composed projection does not hydrate.</exception>
    public WorldDefinition? Disclose(WorldDefinition definition) => Disclose(
        definition: definition,
        tier: Tier
    );
    /// <summary>Discloses a definition as a tier shows this session it: verbatim at
    /// <see cref="WorldDisclosureTier.Replica"/>, a projection composed for the session at
    /// <see cref="WorldDisclosureTier.Presentation"/>, and nothing at <see cref="WorldDisclosureTier.Frames"/>.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="tier">The tier.</param>
    /// <returns>The disclosed definition, or <see langword="null"/> at the frames tier.</returns>
    /// <exception cref="InvalidOperationException">The composed projection does not hydrate.</exception>
    public WorldDefinition? Disclose(WorldDefinition definition, WorldDisclosureTier tier) {
        if (tier == WorldDisclosureTier.Frames) {
            return null;
        }

        if (tier == WorldDisclosureTier.Replica) {
            return definition;
        }

        var time = server.Time;
        var projection = WorldProjection.Compose(
            arena: server.Arena,
            authority: server.AuthorityIdentity,
            definition: definition,
            recipient: observation.Session,
            revision: server.Population.Revision,
            tier: tier,
            time: in time
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
    public void DeliverDefinition(WorldDefinition definition) {
        if (!Discloses()) {
            return;
        }

        Forward(deliver: () => {
            m_withheld = false;
            inner.DeliverDefinition(definition: Disclose(definition: definition)!);
        });
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

        Forward(deliver: () => {
            Resume();
            inner.DeliverSnapshot(snapshot: in redacted);
        });
    }
    // A projection renumbers the rows it keeps, so a state stamp's row ordinals name the authority's rows, not the
    // projection's: below Replica the observer takes the whole disclosed definition instead.
    public void DeliverState(WorldDefinition definition, in WorldStateStamp stamp) {
        if (!Discloses()) {
            return;
        }

        var copy = stamp;

        Forward(deliver: () => {
            if (
                m_withheld ||
                (Tier != WorldDisclosureTier.Replica)
            ) {
                m_withheld = false;
                inner.DeliverDefinition(definition: Disclose(definition: definition)!);

                return;
            }

            inner.DeliverState(
                definition: definition,
                stamp: in copy
            );
        });
    }
}
