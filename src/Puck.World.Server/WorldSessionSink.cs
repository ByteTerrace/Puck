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
/// reach a session. An observer that throws, or that ends its own subscription as an
/// <see cref="IWorldDetachableSink"/>, ends its observation; the hub detaches it.</summary>
internal sealed class WorldSessionSink(WorldServer server, WorldSessionObservation observation, IClientSink inner) : IWorldDetachableSink {
    private readonly IWorldDetachableSink? m_detachable = (inner as IWorldDetachableSink);
    private readonly WorldProjectionFeed m_feed = new(
        recipient: observation.Session,
        seeds: server.ClockSeeds
    );

    private WorldDefinition? m_hydrated;
    private bool m_withheld;
    private bool m_replicaDefined;
    private bool m_deliveringSnapshot;
    private bool m_streamEnded;

    private EntitySnapshot[] m_redacted = [];
    private readonly WorldDeliveryCursor m_cursor = new();
    private readonly WorldDeliveryRows m_rows = new();
    private readonly WorldSolidScaleDisclosure m_scales = new();
    private readonly WorldRecipientView m_view = observation.View;

    private WorldDocumentVersion Version => server.DocumentVersion with { Sequence = m_cursor.Sequence };

    /// <summary>Gets the reason the observer ended its own subscription with, or <see langword="null"/> while it still
    /// takes deliveries; the session ends with it.</summary>
    public string? DetachReason { get; private set; }
    /// <summary>Gets what the observer threw, once it has.</summary>
    public Exception? Fault { get; private set; }

    private bool CanObserve => observation.CanObserve;
    private WorldDisclosureTier Tier => observation.Tier;

    // Queries are read doors into the same disclosed view as deliveries. Only state observations have their own
    // recipient-filtered projection; the remaining readbacks describe authoritative state that a Presentation or
    // redacted Replica mirror does not carry, so they require the whole replica.
    internal bool AllowsQuery(WorldQuery query) => (
        CanObserve &&
        ((query is WorldQuery.StateObservations) || DisclosesFullReplica)
    );

    private bool DisclosesFullReplica => ((Tier == WorldDisclosureTier.Replica) && WorldSessionObservation.RedactsNoBody(server: server));

    // Nothing reaches an observer once it has ended its own subscription.
    private bool Discloses() {
        if (DetachReason is not null) {
            return false;
        }

        if (CanObserve) {
            return true;
        }

        if (!m_withheld) {
            // The stream stops observing: everything it was delivered is lost to it, which its reader is told at once.
            Release();
            try {
                _ = AnnounceCut();
            } catch (Exception exception) {
                EndAfterFault(exception: exception);
            }
        }

        m_withheld = true;

        return false;
    }
    // Ends the observation once its observer takes no more deliveries, by a fault or by its own detach reason: from
    // this moment the session acts no more, and the next step ends it.
    private void EndObservation() {
        observation.MarkEnded(reason: (DetachReason ?? $"world.session.observer-faulted: {Fault?.Message}"));
        server.GrantTable.MarkObserverEnded(session: observation.Session);
        server.NoteObserverEnded(session: observation.Session);
    }
    // Records a delivery fault before it reaches the hub's detach handler. Delivery uses direct calls so a steady
    // snapshot or value-only update does not allocate a capturing delegate.
    private void EndAfterFault(Exception exception) {
        Fault = exception;
        EndObservation();
    }
    // Adopts the reason an observer ended its own subscription with during the delivery just made, so the hub detaches
    // this sink after the same delivery.
    private void FollowObserverDetach() {
        if (
            (DetachReason is null) &&
            (m_detachable?.DetachReason is { } reason)
        ) {
            DetachReason = reason;
            EndObservation();
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
                stream: m_cursor.Stamp(),
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
                stream: m_cursor.Stamp(),
                version: version
            );

            return;
        }

        inner.DeliverDefinition(
            definition: hydrated,
            stream: m_cursor.Stamp(),
            version: version
        );
    }
    // Composes the observer's projection of a delivered document at the tick the world stands at, and presents what it
    // is owed.
    private void Project(WorldDefinition definition, WorldDocumentVersion version) {
        var time = server.DeliveryTime;

        Present(
            delivery: ComposeProjection(),
            engineTick: time.EngineTick,
            tick: time.Tick,
            version: Version
        );
    }
    // Composes what the observer's projection owes; after a cut, its whole projection under the new generation.
    private WorldProjectionDelivery ComposeProjection() {
        var delivery = m_feed.Compose(cursor: m_cursor, view: m_view);

        if (!AnnounceCut()) {
            return delivery;
        }
        m_feed.Release();
        m_hydrated = null;

        return m_feed.Compose(cursor: m_cursor, view: m_view);
    }
    // Sends the record a cut the cursor decided owes, once: the new generation, with no content. True when one was sent,
    // after which the stream owes its whole disclosed document and a primer before anything newer.
    private bool AnnounceCut() {
        if (!m_cursor.TakeCut()) {
            return false;
        }
        inner.DeliverCut(stream: m_cursor.Stamp(), terminal: false);

        return true;
    }
    // Primes a replica observer alone after a cut its snapshot path decided: the whole disclosed document under the new
    // generation, before the snapshot in hand, which completes it as the primer.
    private void PrimeAfterCut() {
        if (!AnnounceCut()) {
            return;
        }
        if (Tier != WorldDisclosureTier.Replica) {
            var time = server.DeliveryTime;

            m_feed.Release();
            m_hydrated = null;
            Present(
                delivery: m_feed.Compose(cursor: m_cursor, view: m_view),
                engineTick: time.EngineTick,
                tick: time.Tick,
                version: Version
            );

            return;
        }
        m_cursor.ForgetDocument();
        if (m_cursor.DeliverDefinition(view: m_view) is { } whole) {
            inner.DeliverDefinition(definition: whole, stream: m_cursor.Stamp(), version: Version);
        }
    }
    /// <summary>Ends the stream: releases what it holds, cuts, and tells the observer the stream ends, so a reader
    /// bound to it lets go of what it showed. Called once the observation has ended; nothing is delivered after
    /// it.</summary>
    public void End() {
        if (m_streamEnded) {
            return;
        }
        m_streamEnded = true;
        Release();
        _ = m_cursor.TakeCut();

        try {
            inner.DeliverCut(stream: m_cursor.Stamp(), terminal: true);
        } catch (Exception exception) {
            Fault ??= exception;
        }
    }
    // Ends a withheld span: the observer takes the current disclosed definition before anything newer.
    private void Resume() {
        if (!m_withheld) {
            return;
        }

        m_withheld = false;

        DeliverDefinition(definition: server.Definition, stream: WorldStreamStamp.Unspanned, version: server.DocumentVersion);

        FollowObserverDetach();
    }

    /// <summary>Releases what the observer's feed holds, its anchor rows included: the observation ended, or its
    /// disclosure is withheld.</summary>
    public void Release() {
        observation.CloseSceneProof(end: server.DeliveryTime.Tick);
        m_feed.Release();
        m_hydrated = null;
        m_cursor.Release();
        server.DetachCursor(observation.Session, m_cursor);
    }

    private WorldDefinition? DiscloseToReplica(WorldDefinition definition) => m_cursor.DeliverDefinition(view: m_view);
    // Delivers the replica document a definition owes the observer, after the record of any cut composing it decided;
    // a cut that composed no document primes the observer alone all the same.
    private void DeliverReplicaDocument(WorldDefinition? delivered) {
        if (delivered is not null) {
            _ = AnnounceCut();
            inner.DeliverDefinition(definition: delivered, stream: m_cursor.Stamp(), version: Version);
            PrimeReplica();
        } else if (m_cursor.TakeCut()) {
            Reprime();
        }
    }
    // Sends a cut the cursor already took, then the whole disclosed document and a primer under its generation.
    private void Reprime() {
        inner.DeliverCut(stream: m_cursor.Stamp(), terminal: false);
        m_cursor.ForgetDocument();
        if (m_cursor.DeliverDefinition(view: m_view) is { } whole) {
            inner.DeliverDefinition(definition: whole, stream: m_cursor.Stamp(), version: Version);
            PrimeReplica();
        }
    }
    // Replacing a replica document replaces its state arena and observation metadata together. Complete that
    // delivery with the existing non-consuming primer now, rather than leaving policy-dependent work for a later
    // quiet tick. The primer has the actual entity table too; an empty synthetic snapshot would erase bodies.
    private void PrimeReplica() {
        // AttachSink supplies the first primer itself. Subsequent definition/state replacements have no such
        // caller, so they must complete the same pair here.
        if (!m_replicaDefined) { m_replicaDefined = true; return; }
        // Resume already has its caller's snapshot in hand. Rebuilding a primer here would overwrite that
        // borrowed carrier while the hub is still fanning it out to other sinks.
        if (m_deliveringSnapshot) { return; }
        FollowObserverDetach();
        if (DetachReason is null) { DeliverSnapshot(snapshot: server.Document.BuildPrimerSnapshot(), stream: WorldStreamStamp.Unspanned); }
    }

    /// <summary>Discloses a definition as this session's tier shows it, for a read door — see
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
    /// <see cref="WorldDisclosureTier.Presentation"/>, and nothing at <see cref="WorldDisclosureTier.Frames"/>. A
    /// one-off composition for a read or a measurement: it holds no anchor and feeds no observer.</summary>
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
            return WorldStateDisclosure.Disclose(arena: arena, definition: definition, recipient: recipient, unrestricted: unrestricted).Definition;
        }

        var time = server.DeliveryTime;
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
    public void DeliverCut(in WorldStreamStamp stream, bool terminal) {
        // The hub's fan-out has one span that never ends; this stream decides its own cuts.
    }
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version, in WorldStreamStamp stream) {
        if (!Discloses()) {
            return;
        }

        try {
            m_withheld = false;
            server.AttachCursor(observation.Session, m_cursor);

            if (Tier == WorldDisclosureTier.Replica) {
                DeliverReplicaDocument(delivered: DiscloseToReplica(definition: definition));
                observation.SettleSceneProof(tick: server.DeliveryTime.Tick);
            } else {
                Project(
                    definition: definition,
                    version: version
                );
            }

            FollowObserverDetach();
        } catch (Exception exception) {
            EndAfterFault(exception: exception);

            throw;
        }
    }
    public void DeliverSessionLever(WorldSessionLever lever) {
    }

    private WorldInputPublication[] m_inputDisclosure = [];

    public void DeliverSnapshot(in WorldSnapshot snapshot, in WorldStreamStamp stream) {
        if (!Discloses()) {
            return;
        }

        var bodies = server.Definition.Population.ObserverDisclosure;

        m_cursor.ObserveBodies(mode: bodies.Mode, radius: bodies.Radius);

        var redacted = WorldOutputHub.Redact(
            disclosure: new WorldSinkDisclosure(
                ObserverBodyIndex: -1,
                Policy: bodies
            ),
            scratch: ref m_redacted,
            snapshot: in snapshot
        );

        var tick = snapshot.Tick;
        var engineTick = snapshot.EngineTick;

        try {
            m_deliveringSnapshot = true;
            Resume();

            if (DetachReason is not null) {
                return;
            }

            // A state clock's anchor reaches the observer ahead of the tick whose phase it holds.
            if (Tier == WorldDisclosureTier.Presentation) {
                Present(
                    delivery: m_feed.Step(cursor: m_cursor, engineTick: engineTick, tick: tick, view: m_view),
                    engineTick: engineTick,
                    tick: tick,
                    version: Version
                );
                FollowObserverDetach();

                if (DetachReason is not null) {
                    return;
                }
            }

            redacted = redacted with {
                FieldCells = m_rows.Advance(m_view, m_cursor, redacted.FieldCells, (Tier == WorldDisclosureTier.Replica)),
                SolidScales = m_scales.Filter(m_view, redacted.SolidScales),
                Inputs = WorldInputPublication.ForOwner(redacted.Inputs, observation.Session, ref m_inputDisclosure),
            };
            // A cut the rows or the body policy decided primes the observer alone, and this snapshot completes it.
            PrimeAfterCut();
            redacted = redacted with { Revision = checked((int)m_cursor.Sequence) };
            redacted = server.PresentCues(redacted, m_view,
                fullBodies: server.Definition.Population.ObserverDisclosure.Mode == WorldObserverDisclosureMode.All);
            inner.DeliverSnapshot(snapshot: in redacted, stream: m_cursor.Stamp());
            observation.SettleSceneProof(tick: tick);
            FollowObserverDetach();
        } catch (Exception exception) {
            EndAfterFault(exception: exception);

            throw;
        } finally {
            m_deliveringSnapshot = false;
        }
    }
    // Below Replica the observer's feed decides what the values owe it: nothing, a delta of the members they moved, or
    // a whole projection after a withheld span. Body redaction does not renumber a Replica's state rows and must not
    // turn value-only updates into structural rebuilds.
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp, in WorldStreamStamp stream) {
        if (!Discloses()) {
            return;
        }

        try {
            server.AttachCursor(observation.Session, m_cursor);
            if (Tier != WorldDisclosureTier.Replica) {
                m_withheld = false;
                Present(
                    delivery: ComposeProjection(),
                    engineTick: stamp.EngineTick,
                    tick: stamp.Tick,
                    version: Version
                );
                FollowObserverDetach();

                return;
            }

            if (m_withheld) {
                m_withheld = false;
                DeliverReplicaDocument(delivered: DiscloseToReplica(definition: definition));
                FollowObserverDetach();

                return;
            }

            // Values travel in the snapshots' cell channel. The document is owed again only when what this
            // recipient's frame reads moved (its placements as it may read them, which a readable dealt cell or a
            // zone move can change without a placement install), never because a hidden-only install happened; the
            // observer's state stands, so it is handed the same section again, never a state exported and loaded
            // whole.
            if (stamp.ArenaValues && !m_cursor.FrameMoved(view: m_view)) { return; }
            if (DiscloseToReplica(definition: definition) is { } delivered) {
                _ = AnnounceCut();
                inner.DeliverState(delivered, Version, stamp with { Everything = true, MovedRows = default, ArenaValues = false }, m_cursor.Stamp());
                PrimeReplica();
            } else if (m_cursor.TakeCut()) {
                Reprime();
            }
            observation.SettleSceneProof(tick: stamp.Tick);
            FollowObserverDetach();
        } catch (Exception exception) {
            EndAfterFault(exception: exception);

            throw;
        }
    }
}
