using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The server's multi-subscriber output publication point. It composes a
/// tick's output for every attached <see cref="IClientSink"/> on the tick thread and hands it over through each
/// subscriber's <see cref="WorldSinkRoute"/>: an <see cref="IWorldInlineSink"/> receives at once, and any other sink
/// receives at once only while the hub's outbox does not record, otherwise when the hub collects. Each subscriber
/// receives the borrowed <see cref="WorldSnapshot"/> (its <see cref="WorldSnapshot.Entries"/> memory wraps a reused
/// server-owned array — see <see cref="WorldServer"/>'s own remarks) and must fully consume or copy it before its
/// <see cref="IClientSink.DeliverSnapshot"/> call returns, because the next tick's snapshot overwrites the same
/// backing array. <see cref="WorldTick.EmitSnapshot"/> only returns once every typed subscriber has done exactly
/// that. Interactive QUIC traffic uses a request-then-response wire
/// (<see cref="WorldPeerWireFormat"/>); continuous federation projections subscribe here and copy each
/// borrowed snapshot into a bounded wire queue before returning.
/// </summary>
/// <remarks><para>Play-and-host (a local sink plus N future connections, plus the tape) is first-class here: every
/// <see cref="Subscribe(IClientSink)"/> call adds a subscriber; it never displaces one already attached.</para>
/// <para><b>Threading contract.</b> This hub carries no lock, so it is safe only when every call — every
/// <see cref="Subscribe(IClientSink)"/>, every disposal of the lease it returns, and every <c>Deliver*</c> — is mutually exclusive
/// with the tick thread's own activity: no such call may ever be in flight at the same time as a <c>Deliver*</c> call
/// or the tick work that leads to one. That is the actual invariant, stated in terms of exclusion rather than thread
/// identity, because two windows legitimately satisfy it without the call literally running on the tick thread while
/// it is ticking: (1) construction-time attach, before the launcher's window-pump/tick thread has started producing
/// any tick — <c>Puck.World.WorldScreenBinder</c>'s boot-loop <c>AttachSink</c> call is this window, single-threaded
/// startup with no tick activity yet to race; and (2) teardown-time detach, after that thread has stopped producing
/// ticks for good — the same binder's <c>Dispose</c> disposing every slot's session lease is this window, host
/// teardown with no tick activity left to race. Both are sanctioned by name. What remains forbidden is a call made
/// during the pump's life from anything other than the tick thread itself — a second thread calling in concurrently
/// with ticking, or a callback re-entering from off that thread
/// while a <c>Deliver*</c> is in flight. Disposing a lease is idempotent and safe between deliveries (the ordinary
/// case — a portal observer detaching once its view closes), and is also safe from within a <c>Deliver*</c> call — a
/// sink disposing its own lease mid-callback, or a fault a few slots back detaching a different one, never corrupts
/// the in-flight fan-out (each <c>Deliver*</c> walks by index and re-checks <c>Active</c> rather than using an
/// enumerator, so no "collection was modified" exception is possible). <see cref="Subscribe(IClientSink)"/> is the one member with
/// a reentrancy rule on top of this: calling it from within a <c>Deliver*</c> callback throws, because the new
/// subscriber's primer would be built into the same borrowed backing array the in-flight snapshot wraps (see
/// <see cref="Subscribe(IClientSink)"/>).</para>
/// <para><b>Exception isolation.</b> A typed subscriber that throws out of any <c>Deliver*</c> call is caught,
/// narrated on stderr by its concrete type, and detached — never retried, and never allowed to unwind into the tick
/// loop and take every other subscriber (and the tick itself) down with it. A broken observer stays broken; it does
/// not get a second chance to corrupt delivery to the healthy ones. A sink whose ending is part of its contract (a full
/// queue, a retired route) implements <see cref="IWorldDetachableSink"/> instead of throwing: it is detached after the
/// delivery that set its reason, with that reason narrated as a detach rather than a fault.</para></remarks>
public sealed partial class WorldOutputHub {
    // One typed-lane slot. Active starts true and is flipped exactly once, by the lease's own Dispose, by a fault caught
    // during delivery, or by the sink's own detach reason; every route is equivalent from the subscriber's point of
    // view (detached, compacted out, never delivered to again). Kept as a class (not a struct) because the lease Subscribe returns IS
    // this object; Dispose closes over it directly rather than needing a separate handle/index to invalidate.
    private sealed class Subscription : IDisposable {
        private readonly WorldOutputHub m_hub;
        // The hub's only hold on the sink: a delivery the outbox recorded that throws on replay detaches the
        // subscription just as one that throws inside the fan-out does.
        public readonly WorldSinkRoute Route;
        public WorldSinkDisclosure Disclosure;
        public Func<WorldSnapshot, WorldSnapshot>? Present;
        public bool Active = true;
        // Whether the sink is an observer whose attention the host cannot read (IWorldWatchingSink).
        public readonly bool Watches;

        public Subscription(WorldOutputHub hub, IClientSink sink, WorldSinkDisclosure disclosure) {
            m_hub = hub;
            Disclosure = disclosure;
            Watches = (sink is IWorldWatchingSink);
            Route = hub.RouteTo(sink: sink, fault: exception => hub.Detach(subscription: this, callSite: "the outbox's collect", exception: exception));
        }

        public void Dispose() => m_hub.Deactivate(subscription: this);
    }

    /// <summary>Body records and their bytes encoded during the last snapshot publication, before sharing.</summary>
    public (long Bodies, long Bytes) EncodedBodies => m_interest.Encoded;

    private readonly WorldInterestFrame m_interest = new();
    private readonly List<Subscription> m_typed = new();

    // Count of subscriptions with Active == true — maintained incrementally (Subscribe increments, a lease's Dispose
    // or a caught fault decrements) so HasTypedSubscribers stays O(1) even while a detached-but-not-yet-compacted
    // slot still physically occupies a place in m_typed between now and the next Deliver* pass.
    private int m_activeCount;
    // Nonzero while a Deliver* fan-out is on the stack. Subscribe refuses while it is — a subscriber attached from
    // inside a delivery callback would have its primer built into the SAME server-owned backing array the in-flight
    // borrowed snapshot wraps, clobbering what later sinks in this very fan-out have not yet consumed.
    private int m_deliveryDepth;

    /// <summary>Gets a value indicating whether at least one typed-lane subscriber is attached — lets a caller skip building a snapshot/answer
    /// nobody would receive. Reflects only active subscribers —
    /// a detached-but-not-yet-compacted slot never counts.</summary>
    public bool HasTypedSubscribers => (m_activeCount > 0);
    /// <summary>Gets the number of active subscribers that watch the world from where its host cannot read their
    /// attention (<see cref="IWorldWatchingSink"/>): while one is attached the host counts the world as watched.</summary>
    public int Watchers => m_watchingCount;

    // Count of active subscriptions whose sink is an IWorldWatchingSink, kept with m_activeCount.
    private int m_watchingCount;

    /// <summary>Gets or sets whether the timeline's deliveries (a definition, a state update, a tick's snapshot) and
    /// the compositions a re-simulated tick re-applies reach no subscriber. A history seek withholds them for its whole
    /// span, so neither its restore, nor a load-door install, nor any re-simulated tick reaches a viewer, and then
    /// delivers the restored timeline once (<see cref="WorldTick.PresentRestoredTimeline"/>). A composition is a
    /// presentation override the history does not rewind, so the viewer keeps the one it holds.</summary>
    internal bool WithholdsTimeline { get; set; }

    // Physically drops every trailing slot a Deliver* pass did not write back (each inactive subscription, whether
    // detached before this pass started, mid-pass by its own lease, or mid-pass by a caught fault) — a single
    // RemoveRange rather than a second List.RemoveAll scan, folded into the SAME walk Deliver* already pays for.
    private void Compact(int writeIndex) {
        if (writeIndex < m_typed.Count) {
            m_typed.RemoveRange(
                index: writeIndex,
                count: (m_typed.Count - writeIndex)
            );
        }
    }
    // Takes a subscription out of every later delivery, exactly once whichever route reaches it first: a sink that
    // disposes its own lease (from inside its delivery, or from a narration sink called while it is being detached)
    // has already decremented m_activeCount, and a second decrement would drift the count low enough that
    // HasTypedSubscribers reads false while healthy subscribers remain, starving them of every snapshot the server
    // then skips building. Every detach runs this before it narrates.
    private void Deactivate(Subscription subscription) {
        if (subscription.Active) {
            subscription.Active = false;
            m_activeCount--;
            if (subscription.Watches) { m_watchingCount--; }
        }
    }
    // Detaches a faulting sink and narrates it loudly (naming its concrete type, never swallowed silently) — a broken
    // observer never gets retried on a later tick. Shared by every Deliver* method's catch block.
    private void Detach(Subscription subscription, string callSite, Exception exception) {
        Deactivate(subscription: subscription);
        Narrate(
            channel: "world.output",
            text: $"[world.output: {subscription.Route.SinkName} threw in {callSite} — detached] {exception}"
        );
    }
    // Detaches a sink that ended its own subscription during the delivery just made, narrating its named reason as an
    // ordinary detach, never as a fault.
    private bool EndedBySink(Subscription subscription) {
        if (subscription.Route.DetachReason is not { } reason) {
            return false;
        }

        Deactivate(subscription: subscription);

        if (HasNarrationSink) {
            Narrate(
                channel: "world.output",
                text: $"[world.output: {subscription.Route.SinkName} detached: {reason}]"
            );
        }

        return true;
    }
    /// <summary>Fans an accepted live window-composition override out to every typed subscriber. A faulting sink is
    /// isolated and detached — see the class remarks.</summary>
    /// <param name="composition">The composition override.</param>
    public void DeliverComposition(WorldComposition composition) {
        if (WithholdsTimeline) {
            return;
        }

        Deliver(
            callSite: nameof(DeliverComposition),
            deliver: static (route, payload) => route.DeliverComposition(composition: payload),
            payload: composition
        );
    }
    /// <summary>Fans the live world definition out to every typed subscriber (once per step with at least one applied
    /// edit, or a definition swap). A faulting sink is isolated and detached — see the class remarks.</summary>
    /// <param name="definition">The definition now live on the server.</param>
    /// <param name="version">The version of <paramref name="definition"/>.</param>
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) {
        if (WithholdsTimeline) {
            return;
        }

        Deliver(
            callSite: nameof(DeliverDefinition),
            deliver: static (route, payload) => route.DeliverDefinition(
                definition: payload.Definition,
                version: payload.Version, stream: WorldStreamStamp.Unspanned
            ),
            payload: (Definition: definition, Version: version)
        );
    }
    /// <summary>Delivers the final creation and shade rows changed during this tick.</summary>
    public void DeliverPresentation(WorldDefinition before, WorldDefinition after, WorldDocumentVersion version) {
        if (WithholdsTimeline) { return; }
        var rows = WorldPresentationRows.Between(before, after);
        Deliver(
            callSite: nameof(DeliverPresentation),
            deliver: static (route, payload) => {
                route.DeliverPresentation(payload.Definition, payload.Rows, payload.Version, WorldStreamStamp.Unspanned);
                if (!route.Detachable) { CountPresentationRows(payload.Rows); }
            },
            payload: (Definition: after, Rows: rows, Version: version)
        );
    }
    internal static void CountPresentationRows(WorldPresentationRows rows) {
        WorldBootWork.Count(WorldBootWork.PresentationDeliveries);
        WorldBootWork.Count(WorldBootWork.PresentationRowsDelivered, rows.Creations.Length + rows.Shades.Length);
        WorldBootWork.Count(WorldBootWork.PresentationBytesDelivered, rows.Payload.Length);
    }
    /// <summary>Fans the live world definition out to every typed subscriber after a value-only mutation (see
    /// <see cref="IClientSink.DeliverState"/>). A faulting sink is isolated and detached — see the class
    /// remarks.</summary>
    /// <param name="definition">The definition now live on the server.</param>
    /// <param name="version">The version of <paramref name="definition"/>.</param>
    /// <param name="stamp">The tick the values hold as of and the rows whose values moved.</param>
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) {
        if (WithholdsTimeline) {
            return;
        }

        Deliver(
            callSite: nameof(DeliverState),
            deliver: static (route, payload) => route.DeliverState(
                definition: payload.Definition,
                stamp: payload.Stamp,
                version: payload.Version, stream: WorldStreamStamp.Unspanned
            ),
            payload: (Definition: definition, Stamp: stamp, Version: version)
        );
    }
    /// <summary>Fans an accepted live session lever out to every typed subscriber. A faulting sink is isolated and
    /// detached — see the class remarks.</summary>
    /// <param name="lever">The accepted lever write.</param>
    public void DeliverSessionLever(WorldSessionLever lever) =>
        Deliver(
            callSite: nameof(DeliverSessionLever),
            deliver: static (route, payload) => route.DeliverSessionLever(lever: payload),
            payload: lever
        );

    // The fan-out every by-value typed Deliver* shares: call `deliver` on each active subscriber's sink, isolating
    // and detaching one that faults, then compact the live list in place. DeliverSnapshot/DeliverAnswer take their
    // payload `in` (a large struct) and DeliverSnapshot also redacts per subscriber, so neither fits this shape.
    private void Deliver<TPayload>(TPayload payload, Action<WorldSinkRoute, TPayload> deliver, string callSite) {
        m_deliveryDepth++;

        try {
            var writeIndex = 0;

            for (var readIndex = 0; (readIndex < m_typed.Count); readIndex++) {
                var subscription = m_typed[readIndex];

                if (!subscription.Active) {
                    continue;
                }

                try {
                    deliver(
                        subscription.Route,
                        payload
                    );
                } catch (Exception exception) {
                    Detach(
                        callSite: callSite,
                        exception: exception,
                        subscription: subscription
                    );
                    continue;
                }

                if (
                    subscription.Active &&
                    !EndedBySink(subscription: subscription)
                ) {
                    m_typed[writeIndex++] = subscription;
                }
            }

            Compact(writeIndex: writeIndex);
        } finally {
            m_deliveryDepth--;
        }
    }

    /// <summary>Fans a tick's snapshot out to every typed subscriber, synchronously, before returning. A faulting
    /// sink is isolated and detached — see the class remarks.</summary>
    /// <param name="snapshot">The tick snapshot.</param>
    public void DeliverSnapshot(in WorldSnapshot snapshot) => DeliverSnapshot(snapshot: in snapshot, lender: null);
    // The tick's fan-out names the owner of the storage its snapshot lends (the document's tick carrier), which a
    // recorded hand-off takes over instead of copying it; a null lender copies.
    internal void DeliverSnapshot(in WorldSnapshot snapshot, IWorldSnapshotLender? lender) {
        if (WithholdsTimeline) {
            return;
        }

        m_interest.Begin(snapshot.Entries);
        m_deliveryDepth++;
        var outer = BeginFanOut(lender: lender);

        try {
            var writeIndex = 0;

            for (var readIndex = 0; (readIndex < m_typed.Count); readIndex++) {
                var subscription = m_typed[readIndex];

                if (!subscription.Active) {
                    continue;
                }

                try {
                    var delivered = subscription.Disclosure.IsFull ? snapshot : snapshot with {
                        Entries = m_interest.Project(subscription.Disclosure), Inputs = default, InputAnswers = default, OwnBodies = default,
                    };
                    if (subscription.Present is { } present) { delivered = present(delivered); }
                    if (subscription.Route.ObservesInterest && subscription.Disclosure.IsFull && subscription.Present is null) {
                        subscription.Route.DeliverSnapshot(delivered, m_interest);
                    } else {
                        subscription.Route.DeliverSnapshot(delivered, WorldStreamStamp.Unspanned);
                    }
                } catch (Exception exception) {
                    Detach(
                        subscription: subscription,
                        callSite: nameof(DeliverSnapshot),
                        exception: exception
                    );
                    continue;
                }

                if (
                    subscription.Active &&
                    !EndedBySink(subscription: subscription)
                ) {
                    m_typed[writeIndex++] = subscription;
                }
            }

            Compact(writeIndex: writeIndex);
        } finally {
            EndFanOut(outer: outer);
            m_deliveryDepth--;
        }
    }
    /// <summary>Adds a typed-lane subscriber, delivered every subsequent tick's output synchronously, on the tick
    /// thread, until either the process ends or the returned lease is disposed.</summary>
    /// <param name="sink">The subscriber to add.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed. Disposal is idempotent and must happen
    /// on the tick thread (see the class remarks). A caller meant to stay attached for the process's whole lifetime
    /// (today's local client sink) deliberately never disposes it — the lease still exists so that choice is a
    /// visible, deliberate leak at the call site rather than an absent capability.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Called from within a <c>Deliver*</c> callback — see the class
    /// remarks' threading contract.</exception>
    public IDisposable Subscribe(IClientSink sink) =>
        Subscribe(
            sink: sink,
            disclosure: WorldSinkDisclosure.Full
        );
    /// <summary>Adds a typed-lane subscriber whose snapshot deliveries are filtered by
    /// <paramref name="disclosure"/> — see <see cref="Subscribe(IClientSink)"/> for the lifetime and threading
    /// contract, which is identical.</summary>
    /// <param name="sink">The subscriber to add.</param>
    /// <param name="disclosure">What this sink's observer is delivered. <see cref="WorldSinkDisclosure.Full"/> is
    /// the unfiltered path, and takes no per-delivery copy at all.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Called from within a <c>Deliver*</c> callback.</exception>
    public IDisposable Subscribe(IClientSink sink, in WorldSinkDisclosure disclosure) => Subscribe(sink, disclosure, null);

    internal IDisposable Subscribe(IClientSink sink, in WorldSinkDisclosure disclosure, Func<WorldSnapshot, WorldSnapshot>? present) =>
        Subscribe(sink, disclosure, present, out _);

    /// <summary>Adds a typed-lane subscriber and hands back the route its attach primer is delivered through, the
    /// same route every later delivery to it takes.</summary>
    internal IDisposable Subscribe(IClientSink sink, in WorldSinkDisclosure disclosure, Func<WorldSnapshot, WorldSnapshot>? present, out WorldSinkRoute route) {
        ArgumentNullException.ThrowIfNull(argument: sink);

        if (m_deliveryDepth != 0) {
            throw new InvalidOperationException(message: "WorldOutputHub.Subscribe was called from within a Deliver* fan-out; attaching mid-delivery would build the new sink's primer into the borrowed snapshot other subscribers are still consuming. Attach before or after a tick's delivery, never during.");
        }

        var subscription = new Subscription(
            disclosure: disclosure,
            hub: this,
            sink: sink
        ) { Present = present };

        route = subscription.Route;
        m_typed.Add(item: subscription);
        m_activeCount++;
        if (subscription.Watches) { m_watchingCount++; }

        return subscription;
    }
}
