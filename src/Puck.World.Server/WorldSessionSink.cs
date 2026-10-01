using Puck.World.Protocol;

using Puck.Commands;

namespace Puck.World.Server;

/// <summary>A session's observation sink: delivers the observer only what the session's admission tier discloses, only
/// while the session holds <c>observe all</c>, and each snapshot redacted under the world's live observer disclosure.
/// A withheld span ends with the current disclosed definition before the next delivery, so the observer never renders
/// a definition it missed an update to. Below the replica tier the observer is a presentation-tier recipient fed by its
/// own <see cref="WorldProjectionFeed"/>: its whole projection first, then only the members that change, and its state
/// clocks' anchors at the ticks its predictions miss; a withheld span or the end of the observation releases what the
/// feed holds. The answers, compositions and levers this world's hub fans out belong to its own clients and never
/// reach a session. An observer that throws ends its observation; the hub detaches it.</summary>
internal sealed class WorldSessionSink(WorldServer server, WorldSessionObservation observation, IClientSink inner) : IClientSink {
    private readonly WorldProjectionFeed m_feed = new(
        recipient: observation.Session,
        seeds: server.ClockSeeds
    );

    private WorldDefinition? m_hydrated;
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

        if (!m_withheld) {
            Release();
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
            server.GrantTable.MarkSessionFaulted(session: observation.Session);
            server.NoteFaultedSession(session: observation.Session);

            throw;
        }
    }
    // Hands a presentation-tier observer what its feed owes: a whole projection as a definition, a delta of values as
    // a state delivery, and any other delta as a definition, each hydrated from the projection the observer now holds.
    private void Present(WorldProjectionDelivery delivery, WorldDocumentVersion version, ulong tick, ulong engineTick) {
        if (delivery.Kind == WorldProjectionDeliveryKind.None) {
            return;
        }

        var projection = delivery.Projection!;

        if (
            (delivery.Kind == WorldProjectionDeliveryKind.Delta) &&
            delivery.TimelineOnly &&
            (m_hydrated is { } held)
        ) {
            m_hydrated = (held with { TimelineRaw = projection.Timeline });
            inner.DeliverState(
                definition: m_hydrated,
                stamp: new WorldStateStamp(
                    EngineTick: engineTick,
                    Everything: false,
                    MovedRows: default,
                    Tick: tick
                ),
                version: version
            );

            return;
        }

        if (!WorldProjection.TryToDefinition(
            definition: out var hydrated,
            projection: projection,
            reason: out var reason
        )) {
            throw new InvalidOperationException(message: $"the projection composed for {observation.Session.Describe()} does not hydrate: {reason}");
        }

        m_hydrated = hydrated;

        if ((delivery.Kind == WorldProjectionDeliveryKind.Delta) && delivery.ValuesOnly) {
            // A projection renumbers the rows it keeps, so the authority's moved ordinals name nothing here.
            inner.DeliverState(
                definition: hydrated,
                stamp: new WorldStateStamp(
                    EngineTick: engineTick,
                    Everything: true,
                    MovedRows: default,
                    Tick: tick
                ),
                version: version
            );

            return;
        }

        inner.DeliverDefinition(
            definition: hydrated,
            version: version
        );
    }
    // Composes the observer's projection of a delivered document at the tick the world stands at, and presents what it
    // is owed.
    private void Project(WorldDefinition definition, WorldDocumentVersion version) {
        var time = server.DeliveryTime;

        Present(
            delivery: m_feed.Compose(
                arena: server.Arena,
                authority: server.AuthorityIdentity,
                definition: definition,
                revision: server.Population.Revision,
                time: in time
            ),
            engineTick: time.EngineTick,
            tick: time.Tick,
            version: version
        );
    }
    // Ends a withheld span: the observer takes the current disclosed definition before anything newer.
    private void Resume() {
        if (!m_withheld) {
            return;
        }

        m_withheld = false;

        if (Tier == WorldDisclosureTier.Replica) {
            inner.DeliverDefinition(
                definition: server.Definition,
                version: server.DocumentVersion
            );

            return;
        }

        Project(
            definition: server.Definition,
            version: server.DocumentVersion
        );
    }

    /// <summary>Releases what the observer's feed holds, its anchor rows included: the observation ended, or its
    /// disclosure is withheld.</summary>
    public void Release() {
        m_feed.Release();
        m_hydrated = null;
    }
    /// <summary>Discloses a definition as a tier shows it: verbatim at <see cref="WorldDisclosureTier.Replica"/>, a
    /// projection composed for <paramref name="recipient"/> from <paramref name="arena"/> at
    /// <see cref="WorldDisclosureTier.Presentation"/>, and nothing at <see cref="WorldDisclosureTier.Frames"/>. A
    /// one-off composition for a measurement: it holds no anchor and feeds no observer.</summary>
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
            ? disclosed
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

        Forward(deliver: () => {
            m_withheld = false;

            if (Tier == WorldDisclosureTier.Replica) {
                inner.DeliverDefinition(
                    definition: definition,
                    version: version
                );

                return;
            }

            Project(
                definition: definition,
                version: version
            );
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
        var tick = snapshot.Tick;
        var engineTick = snapshot.EngineTick;

        Forward(deliver: () => {
            Resume();

            // A state clock's anchor reaches the observer ahead of the tick whose phase it holds.
            if (Tier == WorldDisclosureTier.Presentation) {
                Present(
                    delivery: m_feed.Step(
                        definition: server.Definition,
                        engineTick: engineTick,
                        tick: tick
                    ),
                    engineTick: engineTick,
                    tick: tick,
                    version: server.DocumentVersion
                );
            }

            inner.DeliverSnapshot(snapshot: in redacted);
        });
    }
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) {
        if (!Discloses()) {
            return;
        }

        var copy = stamp;

        Forward(deliver: () => {
            if (Tier != WorldDisclosureTier.Replica) {
                m_withheld = false;
                Present(
                    delivery: m_feed.Compose(
                        arena: server.Arena,
                        authority: server.AuthorityIdentity,
                        definition: definition,
                        revision: server.Population.Revision,
                        time: server.DeliveryTime
                    ),
                    engineTick: copy.EngineTick,
                    tick: copy.Tick,
                    version: version
                );

                return;
            }

            if (m_withheld) {
                m_withheld = false;
                inner.DeliverDefinition(
                    definition: definition,
                    version: version
                );

                return;
            }

            inner.DeliverState(
                definition: definition,
                stamp: in copy,
                version: version
            );
        });
    }
}
