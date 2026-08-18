using System.Numerics;

using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>What one output-hub sink's observer is delivered of each tick's snapshot.</summary>
/// <param name="Policy">The world's authored per-observer disclosure policy.</param>
/// <param name="ObserverBodyIndex">The observer's own 0-based body index, or a negative value when the observer has
/// no body in this world.</param>
public readonly record struct WorldSinkDisclosure(WorldObserverDisclosure Policy, int ObserverBodyIndex) {
    /// <summary>Gets the unfiltered delivery — every active body, with no per-sink copy taken at all. Local and
    /// colocated sinks attach with this: colocated trust is home trust.</summary>
    public static WorldSinkDisclosure Full { get; } = new(
        Policy: WorldObserverDisclosure.Default,
        ObserverBodyIndex: -1
    );
    /// <summary>Gets a value indicating whether this sink receives every active body unfiltered.</summary>
    public bool IsFull => (Policy.Mode == WorldObserverDisclosureMode.All);
}
/// <summary>
/// The server's multi-subscriber output publication point. It fans a
/// tick's output out to every attached <see cref="IClientSink"/> synchronously on the tick thread: each subscriber
/// receives the borrowed <see cref="WorldSnapshot"/> (its <see cref="WorldSnapshot.Entries"/> memory wraps a reused
/// server-owned array — see <see cref="WorldServer"/>'s own remarks) and must fully consume or copy it before its
/// <see cref="IClientSink.DeliverSnapshot"/> call returns, because the next tick's snapshot overwrites the same
/// backing array. <see cref="WorldServer.EmitSnapshot"/> only returns once every typed subscriber has done exactly
/// that. The TCP transport (<see cref="Server.WorldTcpHost"/>) uses its own strictly request-then-response wire
/// instead (<see cref="Server.WorldTcpWireFormat"/>) and never subscribes here.
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
/// not get a second chance to corrupt delivery to the healthy ones.</para></remarks>
public sealed class WorldOutputHub {
    // One typed-lane slot. Active starts true and is flipped exactly once, either by the lease's own Dispose or by a
    // fault caught during delivery — both routes are equivalent from the subscriber's point of view (detached,
    // compacted out, never delivered to again). Kept as a class (not a struct) because the lease Subscribe returns IS
    // this object; Dispose closes over it directly rather than needing a separate handle/index to invalidate.
    private sealed class Subscription(WorldOutputHub hub, IClientSink sink, WorldSinkDisclosure disclosure) : IDisposable {
        public readonly IClientSink Sink = sink;
        public WorldSinkDisclosure Disclosure = disclosure;
        // Per-sink redaction scratch, grown once to the widest snapshot this sink ever saw. Never shared with the
        // server's own borrowed backing array, which the redacted delivery must not write into.
        public EntitySnapshot[] Redacted = [];
        public bool Active = true;

        public ref WorldSinkDisclosure DisclosureRef => ref Disclosure;

        public void Dispose() {
            if (!Active) {
                return;
            }

            Active = false;
            hub.m_activeCount--;
        }
    }

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
    // Narrates a faulting sink loudly (naming its concrete type, never swallowed silently) and detaches it — a
    // broken observer never gets retried on a later tick. Shared by every Deliver* method's catch block. The Active
    // guard is load-bearing, not defensive style: a sink that disposes its OWN lease and then throws has already
    // decremented m_activeCount once through Dispose, and a second decrement here would drift the count low enough
    // that HasTypedSubscribers reads false while healthy subscribers remain — silently starving them of every
    // subsequent snapshot the server then skips building.
    private void Detach(Subscription subscription, string callSite, Exception exception) {
        Console.Error.WriteLine(value: $"[world.output: {subscription.Sink.GetType().Name} threw in {callSite} — detached] {exception}");

        if (subscription.Active) {
            subscription.Active = false;
            m_activeCount--;
        }
    }
    private static WorldSnapshot Redact(Subscription subscription, in WorldSnapshot snapshot) =>
        Redact(
            disclosure: in subscription.DisclosureRef,
            snapshot: in snapshot,
            scratch: ref subscription.Redacted
        );

    /// <summary>Fans a composed query answer out to every typed subscriber. A faulting sink is isolated and
    /// detached — see the class remarks.</summary>
    /// <param name="answer">The answer.</param>
    public void DeliverAnswer(in QueryAnswer answer) {
        m_deliveryDepth++;

        try {
            var writeIndex = 0;

            for (var readIndex = 0; (readIndex < m_typed.Count); readIndex++) {
                var subscription = m_typed[readIndex];

                if (!subscription.Active) {
                    continue;
                }

                try {
                    subscription.Sink.DeliverAnswer(answer: in answer);
                } catch (Exception exception) {
                    Detach(
                        subscription: subscription,
                        callSite: nameof(DeliverAnswer),
                        exception: exception
                    );
                    continue;
                }

                if (subscription.Active) {
                    m_typed[writeIndex++] = subscription;
                }
            }

            Compact(writeIndex: writeIndex);
        } finally {
            m_deliveryDepth--;
        }
    }
    /// <summary>Fans an accepted live window-composition override out to every typed subscriber. A faulting sink is
    /// isolated and detached — see the class remarks.</summary>
    /// <param name="composition">The composition override.</param>
    public void DeliverComposition(WorldComposition composition) =>
        Deliver(
            callSite: nameof(DeliverComposition),
            deliver: static (sink, payload) => sink.DeliverComposition(composition: payload),
            payload: composition
        );
    /// <summary>Fans the live world definition out to every typed subscriber (once per step with at least one applied
    /// edit, or a definition swap). A faulting sink is isolated and detached — see the class remarks.</summary>
    /// <param name="definition">The definition now live on the server.</param>
    public void DeliverDefinition(WorldDefinition definition) =>
        Deliver(
            callSite: nameof(DeliverDefinition),
            deliver: static (sink, payload) => sink.DeliverDefinition(definition: payload),
            payload: definition
        );
    /// <summary>Fans an accepted live session lever out to every typed subscriber. A faulting sink is isolated and
    /// detached — see the class remarks.</summary>
    /// <param name="lever">The accepted lever write.</param>
    public void DeliverSessionLever(WorldSessionLever lever) =>
        Deliver(
            callSite: nameof(DeliverSessionLever),
            deliver: static (sink, payload) => sink.DeliverSessionLever(lever: payload),
            payload: lever
        );

    // The fan-out every by-value typed Deliver* shares: call `deliver` on each active subscriber's sink, isolating
    // and detaching one that faults, then compact the live list in place. DeliverSnapshot/DeliverAnswer take their
    // payload `in` (a large struct) and DeliverSnapshot also redacts per subscriber, so neither fits this shape.
    private void Deliver<TPayload>(TPayload payload, Action<IClientSink, TPayload> deliver, string callSite) {
        m_deliveryDepth++;

        try {
            var writeIndex = 0;

            for (var readIndex = 0; (readIndex < m_typed.Count); readIndex++) {
                var subscription = m_typed[readIndex];

                if (!subscription.Active) {
                    continue;
                }

                try {
                    deliver(subscription.Sink, payload);
                } catch (Exception exception) {
                    Detach(
                        callSite: callSite,
                        exception: exception,
                        subscription: subscription
                    );
                    continue;
                }

                if (subscription.Active) {
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
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        m_deliveryDepth++;

        try {
            var writeIndex = 0;

            for (var readIndex = 0; (readIndex < m_typed.Count); readIndex++) {
                var subscription = m_typed[readIndex];

                if (!subscription.Active) {
                    continue;
                }

                try {
                    if (subscription.Disclosure.IsFull) {
                        subscription.Sink.DeliverSnapshot(snapshot: in snapshot);
                    } else {
                        var redacted = Redact(
                            snapshot: in snapshot,
                            subscription: subscription
                        );

                        subscription.Sink.DeliverSnapshot(snapshot: in redacted);
                    }
                } catch (Exception exception) {
                    Detach(
                        subscription: subscription,
                        callSite: nameof(DeliverSnapshot),
                        exception: exception
                    );
                    continue;
                }

                if (subscription.Active) {
                    m_typed[writeIndex++] = subscription;
                }
            }

            Compact(writeIndex: writeIndex);
        } finally {
            m_deliveryDepth--;
        }
    }
    /// <summary>Builds one observer's own view of a tick. The observer's position is read from the same snapshot, so
    /// a radius test answers against the tick being delivered rather than a pose the hub kept.</summary>
    /// <param name="disclosure">What the observer is delivered.</param>
    /// <param name="snapshot">The tick's full snapshot.</param>
    /// <param name="scratch">The caller-owned destination array, grown as needed. Never the server's own borrowed
    /// backing array — the redacted delivery writes into this.</param>
    /// <returns>The redacted snapshot, wrapping <paramref name="scratch"/>.</returns>
    public static WorldSnapshot Redact(in WorldSinkDisclosure disclosure, in WorldSnapshot snapshot, ref EntitySnapshot[] scratch) {
        var entries = snapshot.Entries.Span;
        var observerIndex = disclosure.ObserverBodyIndex;
        var observerPosition = Vector3.Zero;

        for (var index = 0; (index < entries.Length); index++) {
            if (entries[index].Index == observerIndex) {
                observerPosition = entries[index].Position;

                break;
            }
        }

        if (scratch.Length < entries.Length) {
            scratch = new EntitySnapshot[entries.Length];
        }

        var count = 0;

        for (var index = 0; (index < entries.Length); index++) {
            if (disclosure.Policy.Discloses(
                entry: in entries[index],
                observerIndex: observerIndex,
                observerPosition: observerPosition
            )) {
                scratch[count++] = entries[index];
            }
        }

        return (snapshot with {
            Entries = scratch.AsMemory(
            length: count,
            start: 0
        ),
        });
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
    public IDisposable Subscribe(IClientSink sink, in WorldSinkDisclosure disclosure) {
        ArgumentNullException.ThrowIfNull(argument: sink);

        if (m_deliveryDepth != 0) {
            throw new InvalidOperationException(message: "WorldOutputHub.Subscribe was called from within a Deliver* fan-out; attaching mid-delivery would build the new sink's primer into the borrowed snapshot other subscribers are still consuming. Attach before or after a tick's delivery, never during.");
        }

        var subscription = new Subscription(
            disclosure: disclosure,
            hub: this,
            sink: sink
        );

        m_typed.Add(item: subscription);
        m_activeCount++;

        return subscription;
    }
}
