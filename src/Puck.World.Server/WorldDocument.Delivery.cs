using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // A shape change primes the document. Smaller edits deliver the creation, shade and value rows they touched.
    private bool m_pendingDefinitionDelivery;
    private bool m_pendingStateDelivery;
    private WorldDefinition? m_pendingPresentationBasis;

    /// <summary>Delivers whatever the step installed and has not yet delivered: the definition after a shape change,
    /// or the state after a value change, stamped with the tick and the rows the export sweep found moved.</summary>
    internal void DeliverPending() {
        if (m_pendingDefinitionDelivery) {
            Host.ForgetMovedRows();
            Host.Output.DeliverDefinition(
                definition: m_definition,
                version: Host.DocumentVersion
            );
        } else {
            if (m_pendingPresentationBasis is { } basis) {
                Host.Output.DeliverPresentation(basis, m_definition, Host.DocumentVersion);
            }
            if (m_pendingStateDelivery) {
                var stamp = Host.TakeStateStamp() with { ArenaValues = true };
                Host.Output.DeliverState(m_definition, Host.DocumentVersion, in stamp);
                Host.ForgetMovedRows();
            }
        }

        m_pendingDefinitionDelivery = false;
        m_pendingStateDelivery = false;
        m_pendingPresentationBasis = null;
    }

    /// <summary>Records that the live definition was replaced since the last delivery, so
    /// <see cref="DeliverPending"/> carries it whole to every attached sink. Every way the authoritative state jumps
    /// rather than steps reaches here: a shape mutation's install and every whole-document rebuild
    /// (<c>world.reset</c>, <c>world.load</c>, <c>world.reload</c>, a replay drive's boot image) through
    /// <see cref="Install"/>, an undo through the same install, a machine operation's adoption, and a checkpoint
    /// restore (a direct restore or a replay drive), which delivers at once because it completes
    /// outside the tick. A projection recipient reads its observations from that delivery: its per-tick step sends
    /// anchors alone, so a jump nothing delivered would leave it presenting the state the jump replaced.</summary>
    internal void MarkDefinitionDeliveryPending() => m_pendingDefinitionDelivery = true;
    /// <summary>Records that the live definition's state values, or what a recipient may read of them (an authority
    /// transition), have moved since the last delivery, so the step's <see cref="DeliverPending"/> carries them to every
    /// attached sink; each recipient's stream decides whether it owes its reader the disclosed document again.</summary>
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
    /// <param name="present">The local presentation's recipient filter for ordinary ticks.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    internal IDisposable AttachSink(IClientSink sink, in WorldSinkDisclosure disclosure, Func<WorldSnapshot, WorldSnapshot>? present = null) {
        ArgumentNullException.ThrowIfNull(argument: sink);

        var lease = Host.Output.Subscribe(
            disclosure: in disclosure,
            present: present,
            sink: sink,
            route: out var route
        );

        // Both the definition and the primer go to the NEWLY attached sink only (not a hub-wide broadcast) — an
        // already-attached sink must not replay a stale definition/snapshot every time a later sink joins. Isolated
        // the SAME way WorldOutputHub isolates an ordinary tick delivery fault (its own remarks): a sink that throws
        // during its own attach primer must not take down whoever called AttachSink, and is detached before it ever
        // reaches an ordinary tick delivery.
        // A sink that ends its own subscription on the definition is handed no primer snapshot after it. The primer
        // takes the subscription's own route, so a sink the outbox defers is primed in the same order as everything
        // after it.
        try {
            route.DeliverDefinition(definition: m_definition, version: Host.DocumentVersion, stream: WorldStreamStamp.Unspanned);

            if (route.DetachReason is null) {
                var primer = BuildPrimerSnapshot();

                if (disclosure.IsFull) {
                    route.DeliverSnapshot(snapshot: in primer, stream: WorldStreamStamp.Unspanned);
                } else {
                    var frame = WorldInterestFrame.ForPrimer(primer);
                    var redacted = primer with { Entries = frame.Project(disclosure), Inputs = default, InputAnswers = default };

                    route.DeliverSnapshot(snapshot: in redacted, stream: WorldStreamStamp.Unspanned);
                }
            }

            if (route.DetachReason is { } reason) {
                lease.Dispose();

                if (Host.Output.HasNarrationSink) {
                    Host.Output.Narrate(channel: "world.output", text: $"[world.output: {route.SinkName} detached during its own attach primer: {reason}]");
                }
            }
        } catch (Exception exception) {
            if (Host.Output.HasNarrationSink) {
                Host.Output.Narrate(channel: "world.output", text: $"[world.output: {route.SinkName} threw during its own attach primer — detached] {exception}");
            }
            lease.Dispose();
        }

        return lease;
    }
}
