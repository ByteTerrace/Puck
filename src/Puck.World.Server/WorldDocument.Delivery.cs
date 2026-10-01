using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // What the tick has installed and not yet delivered: a shape change carries the definition, a value change
    // carries state; the step delivers whichever is pending, once, through DeliverPending.
    private bool m_pendingDefinitionDelivery;
    private bool m_pendingStateDelivery;

    /// <summary>Delivers whatever the step installed and has not yet delivered: the definition after a shape change,
    /// or the state after a value change, stamped with the tick and the rows the export sweep found moved.</summary>
    internal void DeliverPending() {
        if (m_pendingDefinitionDelivery) {
            Host.ForgetMovedRows();
            Host.Output.DeliverDefinition(
                definition: m_definition,
                version: Host.DocumentVersion
            );
        } else if (m_pendingStateDelivery) {
            var stamp = Host.TakeStateStamp();

            Host.Output.DeliverState(
                definition: m_definition,
                stamp: in stamp,
                version: Host.DocumentVersion
            );
            Host.ForgetMovedRows();
        }

        m_pendingDefinitionDelivery = false;
        m_pendingStateDelivery = false;
    }
    /// <summary>Records that the live definition's state values have moved since the last delivery, so the step's
    /// <see cref="DeliverPending"/> carries them to every attached sink.</summary>
    /// <remarks>A shape change already pending outranks this: a definition delivery carries the values too.</remarks>
    internal void MarkStateDeliveryPending() => m_pendingStateDelivery = true;
    /// <summary>Attaches a client sink the per-tick snapshot is delivered to, immediately delivering the live
    /// definition followed by a primer snapshot of the current table, so the client renders the current state before
    /// its first ordinary tick delivery. A subscribe, not an overwrite: <see cref="WorldOutputHub"/> supports more
    /// than one attached sink (play-and-host — a local sink plus N future connections plus the tape all
    /// subscribing), so a second call adds a second subscriber rather than displacing the first.</summary>
    /// <param name="sink">The sink to deliver snapshots to.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed — see
    /// <see cref="WorldOutputHub.Subscribe(IClientSink)"/> for the threading/idempotency contract. Disposal takes the sink out of
    /// every future delivery; it never retracts what the primer or an earlier tick already delivered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    internal IDisposable AttachSink(IClientSink sink) =>
        AttachSink(
            sink: sink,
            disclosure: WorldSinkDisclosure.Full
        );
    /// <summary>Attaches a sink whose snapshot deliveries are filtered by <paramref name="disclosure"/> — see
    /// <see cref="AttachSink(IClientSink)"/> for the lifetime contract, which is identical. The attach primer is
    /// filtered the same way an ordinary tick's delivery is, so a redacted sink never sees an unredacted first
    /// frame.</summary>
    /// <param name="sink">The sink to deliver snapshots to.</param>
    /// <param name="disclosure">What this sink's observer is delivered.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    internal IDisposable AttachSink(IClientSink sink, in WorldSinkDisclosure disclosure) {
        ArgumentNullException.ThrowIfNull(argument: sink);

        var lease = Host.Output.Subscribe(
            disclosure: in disclosure,
            sink: sink
        );

        // Both the definition and the primer go to the NEWLY attached sink only (not a hub-wide broadcast) — an
        // already-attached sink must not replay a stale definition/snapshot every time a later sink joins. Isolated
        // the SAME way WorldOutputHub isolates an ordinary tick delivery fault (its own remarks): a sink that throws
        // during its own attach primer must not take down whoever called AttachSink, and is detached before it ever
        // reaches an ordinary tick delivery.
        // A sink that ends its own subscription on the definition is handed no primer snapshot after it.
        var detachable = (sink as IWorldDetachableSink);

        try {
            sink.DeliverDefinition(definition: m_definition, version: Host.DocumentVersion);

            if (detachable?.DetachReason is null) {
                var primer = BuildPrimerSnapshot();

                if (disclosure.IsFull) {
                    sink.DeliverSnapshot(snapshot: in primer);
                } else {
                    var scratch = Array.Empty<EntitySnapshot>();
                    var redacted = WorldOutputHub.Redact(
                        disclosure: in disclosure,
                        scratch: ref scratch,
                        snapshot: in primer
                    );

                    sink.DeliverSnapshot(snapshot: in redacted);
                }
            }

            if (detachable?.DetachReason is { } reason) {
                lease.Dispose();

                if (Host.Output.HasNarrationSink) {
                    Host.Output.Narrate(channel: "world.output", text: $"[world.output: {sink.GetType().Name} detached during its own attach primer: {reason}]");
                }
            }
        } catch (Exception exception) {
            if (Host.Output.HasNarrationSink) {
                Host.Output.Narrate(channel: "world.output", text: $"[world.output: {sink.GetType().Name} threw during its own attach primer — detached] {exception}");
            }
            lease.Dispose();
        }

        return lease;
    }
}
