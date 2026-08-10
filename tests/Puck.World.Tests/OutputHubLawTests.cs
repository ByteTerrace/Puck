using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// Proves the observation-lifetime repair the portal campaign's Campaign 1 item 4 owes
/// (<c>docs/world-model.md</c>'s "Observation and display" section): a typed-lane subscription is independently
/// disposable, a late attach's non-consuming primer never steals a one-shot continuity hint an already-attached sink
/// is due to observe, and a faulting sink is isolated rather than taking the tick down with it. All three exercise
/// <see cref="Puck.World.Server.WorldServer.AttachSink"/>/<see cref="Puck.World.Server.WorldOutputHub"/> directly
/// against a fresh in-process server — no console, no transport.
/// </summary>
public sealed class OutputHubLawTests {
    [Fact]
    public void DisposingOneLease_DoesNotAffectAnothersDelivery() {
        using var fixture = Fixtures.FreshServer();

        var sinkA = new RecordingSink();
        var sinkB = new RecordingSink();
        var leaseA = fixture.Server.AttachSink(sink: sinkA);
        using var leaseB = fixture.Server.AttachSink(sink: sinkB);

        // Each AttachSink call already delivered one primer snapshot to its own sink only.
        Assert.Equal(expected: 1, actual: sinkA.SnapshotDeliveries);
        Assert.Equal(expected: 1, actual: sinkB.SnapshotDeliveries);

        fixture.Step();
        Assert.Equal(expected: 2, actual: sinkA.SnapshotDeliveries);
        Assert.Equal(expected: 2, actual: sinkB.SnapshotDeliveries);

        leaseA.Dispose();
        // Idempotent — a second Dispose of the same lease must not throw or double-decrement anything observable.
        leaseA.Dispose();

        fixture.Step();
        fixture.Step();

        // A now stopped receiving the moment its lease was disposed; B, never touched, kept receiving every tick.
        Assert.Equal(expected: 2, actual: sinkA.SnapshotDeliveries);
        Assert.Equal(expected: 4, actual: sinkB.SnapshotDeliveries);
    }

    [Fact]
    public void LateAttachPrimerDoesNotConsumeContinuityAPriorSinkObserves() {
        using var fixture = Fixtures.FreshServer();

        var priorSink = new RecordingSink();
        using var priorLease = fixture.Server.AttachSink(sink: priorSink);

        // Activate body 0 (Fixtures.FreshServer boots every seat unjoined — see EngageAuthorityLawTests' own
        // remarks) so it appears in the snapshot's entries at all.
        var actor = WorldPrincipal.Seat(slot: 0);
        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey));

        // Stamp body 0 with a hard-teleport continuity hint through the ordinary authoritative door — the ONE-SHOT
        // flag BuildSnapshot's ordinary (consuming) path is due to report on the NEXT tick's broadcast.
        fixture.Server.Body(index: 0)!.Pose(x: 5f, y: 0f, z: 5f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        // The late attach: its own primer must PEEK, not consume — see WorldServer.BuildPrimerSnapshot.
        var lateSink = new RecordingSink();
        using var lateLease = fixture.Server.AttachSink(sink: lateSink);

        // The ordinary next-tick broadcast — the first point a CONSUMING read of body 0's continuity is allowed.
        fixture.Step();

        var priorContinuity = priorSink.LastContinuity(index: 0);
        Assert.NotNull(@object: priorContinuity);
        Assert.Equal(expected: EntityContinuityKind.Teleport, actual: priorContinuity!.Value.Kind);

        // The late sink observes the SAME tick's fact too — the primer's peek did not desynchronize it either.
        var lateContinuity = lateSink.LastContinuity(index: 0);
        Assert.NotNull(@object: lateContinuity);
        Assert.Equal(expected: EntityContinuityKind.Teleport, actual: lateContinuity!.Value.Kind);
    }

    [Fact]
    public void FaultingSinkIsDetached_LaterTicksStillDeliverToHealthySinks() {
        using var fixture = Fixtures.FreshServer();

        // Succeeds on its own primer (call #1), throws on every delivery from the first ordinary tick onward.
        var faulting = new FaultingSink(throwFromCall: 2);
        var healthy = new RecordingSink();

        _ = fixture.Server.AttachSink(sink: faulting);
        using var healthyLease = fixture.Server.AttachSink(sink: healthy);

        Assert.Equal(expected: 1, actual: faulting.Attempts);
        Assert.Equal(expected: 1, actual: healthy.SnapshotDeliveries);

        fixture.Step();

        // The fault was isolated: the healthy sink still received this tick's delivery.
        Assert.Equal(expected: 2, actual: healthy.SnapshotDeliveries);
        Assert.Equal(expected: 2, actual: faulting.Attempts);

        fixture.Step();
        fixture.Step();

        // The faulting sink was detached after its ONE throw — never retried — while the healthy sink kept going.
        Assert.Equal(expected: 2, actual: faulting.Attempts);
        Assert.Equal(expected: 4, actual: healthy.SnapshotDeliveries);
    }

    [Fact]
    public void SelfDisposingThenThrowingSink_DoesNotStarveHealthySubscribers() {
        using var fixture = Fixtures.FreshServer();

        var selfDisposing = new SelfDisposingThrowingSink();
        var healthy = new RecordingSink();

        selfDisposing.Lease = fixture.Server.AttachSink(sink: selfDisposing);
        using var healthyLease = fixture.Server.AttachSink(sink: healthy);

        fixture.Step();
        fixture.Step();
        fixture.Step();

        // The first ordinary tick made the offender dispose its OWN lease and then throw out of the same delivery.
        // Without Detach's Active guard that pairing decremented the active count twice, HasTypedSubscribers read
        // false with a healthy subscriber still attached, and the server silently stopped building snapshots at all —
        // the healthy sink must instead keep receiving every subsequent tick.
        Assert.Equal(expected: 4, actual: healthy.SnapshotDeliveries);
    }

    [Fact]
    public void AttachingFromWithinADeliveryCallback_IsRefusedWithoutCorruptingTheFanOut() {
        using var fixture = Fixtures.FreshServer();

        var reentrant = new ReattachingSink(server: fixture.Server);
        var healthy = new RecordingSink();

        _ = fixture.Server.AttachSink(sink: reentrant);
        using var healthyLease = fixture.Server.AttachSink(sink: healthy);

        fixture.Step();
        fixture.Step();

        // The mid-delivery attach was refused (WorldOutputHub.Subscribe throws under a live fan-out — the smuggled
        // sink's primer would have been built into the borrowed snapshot the fan-out was still delivering), which
        // detached the offender through the ordinary fault path; the smuggled sink was never subscribed and the
        // healthy sink's deliveries were untouched.
        Assert.Equal(expected: 3, actual: healthy.SnapshotDeliveries);
        Assert.Equal(expected: 0, actual: reentrant.Smuggled.SnapshotDeliveries);
    }
}

/// <summary>A typed-lane sink test double: copies every delivered snapshot's entries (never retains the borrowed
/// server-owned memory past its own <see cref="DeliverSnapshot"/> call, honoring the hub's own borrowed-snapshot
/// contract) and counts deliveries.</summary>
internal sealed class RecordingSink : IClientSink {
    public int SnapshotDeliveries { get; private set; }
    private EntitySnapshot[] m_lastEntries = [];

    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        SnapshotDeliveries++;
        m_lastEntries = snapshot.Entries.ToArray();
    }

    public void DeliverAnswer(in QueryAnswer answer) { }

    public void DeliverDefinition(WorldDefinition definition) { }

    public void DeliverComposition(WorldComposition composition) { }

    public void DeliverSessionLever(WorldSessionLever lever) { }

    /// <summary>The most recently delivered snapshot's continuity hint for the named entity index, or
    /// <see langword="null"/> when no delivery has reported that index active.</summary>
    public EntityContinuity? LastContinuity(int index) {
        foreach (var entry in m_lastEntries) {
            if (entry.Index == index) {
                return entry.Continuity;
            }
        }

        return null;
    }
}

/// <summary>A typed-lane sink test double that disposes its OWN lease and then throws, from its first ordinary tick
/// delivery onward (the attach primer is benign) — the exact pairing that once double-decremented the hub's active
/// count through Dispose and the fault-detach both.</summary>
internal sealed class SelfDisposingThrowingSink : IClientSink {
    /// <summary>The sink's own lease, assigned by the test right after <c>AttachSink</c> returns it.</summary>
    public IDisposable? Lease;
    private int m_attempts;

    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        m_attempts++;

        if (m_attempts >= 2) {
            Lease?.Dispose();
            throw new InvalidOperationException(message: "SelfDisposingThrowingSink: deliberate dispose-then-throw for OutputHubLawTests.");
        }
    }

    public void DeliverAnswer(in QueryAnswer answer) { }

    public void DeliverDefinition(WorldDefinition definition) { }

    public void DeliverComposition(WorldComposition composition) { }

    public void DeliverSessionLever(WorldSessionLever lever) { }
}

/// <summary>A typed-lane sink test double that calls <c>AttachSink</c> from within its first ordinary tick delivery
/// (the attach primer is benign), attempting to smuggle a second sink into the live fan-out — the reentrancy
/// <c>WorldOutputHub.Subscribe</c> refuses by throwing.</summary>
internal sealed class ReattachingSink(Puck.World.Server.WorldServer server) : IClientSink {
    /// <summary>The sink the reentrant attach tries to smuggle in — must never receive a delivery.</summary>
    public RecordingSink Smuggled { get; } = new();
    private int m_attempts;

    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        m_attempts++;

        if (m_attempts >= 2) {
            _ = server.AttachSink(sink: Smuggled);
        }
    }

    public void DeliverAnswer(in QueryAnswer answer) { }

    public void DeliverDefinition(WorldDefinition definition) { }

    public void DeliverComposition(WorldComposition composition) { }

    public void DeliverSessionLever(WorldSessionLever lever) { }
}

/// <summary>A typed-lane sink test double that throws out of <see cref="DeliverSnapshot"/> from
/// <paramref name="throwFromCall"/> onward (1-based call count) — proves <c>WorldOutputHub</c>'s per-sink exception
/// isolation without needing the sink to fail on its own <c>AttachSink</c> primer too.</summary>
internal sealed class FaultingSink(int throwFromCall) : IClientSink {
    public int Attempts { get; private set; }

    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        Attempts++;

        if (Attempts >= throwFromCall) {
            throw new InvalidOperationException(message: "FaultingSink: deliberate delivery fault for OutputHubLawTests.");
        }
    }

    public void DeliverAnswer(in QueryAnswer answer) { }

    public void DeliverDefinition(WorldDefinition definition) { }

    public void DeliverComposition(WorldComposition composition) { }

    public void DeliverSessionLever(WorldSessionLever lever) { }
}
