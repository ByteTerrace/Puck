using Xunit;

using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldPopulation.TryDetachSeatForTransfer"/>/<see cref="WorldPopulation.RestoreDetachedSeat"/>'s
/// widened abort-exactness (a same-process transfer's abort must restore EXACT original state, not merely
/// position/yaw) — the ratified in-flight rule docs/vision.md states: "drop and re-derive what the engine can
/// recompute; carry what the player can perceive". <c>Puck.World</c> (the composition root, home of
/// <c>WorldInstanceHost.ApplyTransfer</c> — the actual abort/restore CALLER) is out of reach for this project (see
/// Fixtures.cs's own remarks), so this suite proves the PRIMITIVE the abort path depends on: detaching a body with
/// live dynamic state and restoring it reproduces that state exactly, and never reinstates park. The end-to-end
/// abort-refire behavior (a restored body's stale origin must not re-trigger the portal it just departed) is
/// verified by RUNNING <c>Puck.World</c> (CLAUDE.md rule 3 — game features are not gated), per the campaign's own
/// VERIFY section.
/// </summary>
public sealed class TransferAbortDynamicStateLawTests {
    [Fact]
    public void DetachThenRestore_DynamicState_RoundTripsExactly_AndNeverReinstatesPark() {
        using var fixture = Fixtures.FreshServer();
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var population = fixture.Server.Population;
        var body = fixture.Server.Body(index: actor.Index)!;

        // Drive the body into a genuinely non-rest dynamic state before capturing anything — a discriminating case
        // (Laws' own doctrine): a body already at rest would prove nothing about whether velocity/action-track state
        // actually rides through the capture/restore seam versus simply defaulting to the same zero either way.
        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: 0, value: FixedQ4816.One)); // "forward" role channel (Fixtures' one declared channel at ordinal 0)
        fixture.Step(); // grounded model, empty response table: planar velocity snaps to the commanded target THIS step; gravity (no collider in this fixture) integrates a non-zero vertical velocity from tick 1 on (see PortalSweepOriginLawTests' own remarks).
        // A timed channel press in flight — WorldBody.PressChannel reaches ANY ordinal directly, no kit binding
        // required, so this exercises the action-track capture without needing to author a bound action instruction.
        var pressOutcome = body.PressChannel(ordinal: 1, value: FixedQ4816.One, holdSeconds: 5f, authoredMaximum: FixedQ4816.FromInteger(value: 60));

        Assert.Equal(expected: PressHoldCapKind.None, actual: pressOutcome.CapKind);

        var capturedPosition = body.FixedPosition;
        var capturedYaw = body.FixedYaw;
        var capturedOrientation = body.FixedOrientation;
        var capturedState = body.CaptureTransferState();

        Assert.NotEqual(expected: FixedQ4816.Zero, actual: capturedState.VerticalVelocity);
        Assert.True(condition: (capturedState.ChannelTimerTicks[1] > 0), userMessage: "the in-flight timed press must have a live remaining-ticks countdown to capture");
        Assert.Equal(expected: FixedQ4816.One, actual: capturedState.ChannelTimerValues[1]);

        // LEAVE — the ordinary transfer detach. Only the seat binding and Profile survive THIS call by design (see
        // TryDetachSeatForTransfer's own remarks); everything else on the OLD body object is about to be discarded.
        Assert.True(condition: population.TryDetachSeatForTransfer(slot: actor.Index, profile: out var profile));
        Assert.False(condition: population.IsActive(index: actor.Index));

        // ABORT — restore onto the SAME seat at the EXACT captured pose plus the widened dynamic-state capture.
        Assert.True(condition: population.RestoreDetachedSeat(slot: actor.Index, profile: profile, position: capturedPosition, yawRadians: capturedYaw, dynamicState: capturedState));

        var restoredBody = fixture.Server.Body(index: actor.Index)!;

        // Pose — the pre-existing exactness contract, unchanged.
        Assert.Equal(expected: capturedPosition, actual: restoredBody.FixedPosition);
        Assert.Equal(expected: capturedYaw, actual: restoredBody.FixedYaw);
        Assert.Equal(expected: capturedOrientation, actual: restoredBody.FixedOrientation);
        // The abort-refire invariant's own consequence: Pose's CommitTeleport collapses the swept segment's start to
        // the landing point, so a portal scan immediately after sees a degenerate point at the CURRENT position —
        // never a ghost segment sweeping back from where the body used to be.
        Assert.Equal(expected: capturedPosition, actual: restoredBody.FixedPreviousPosition);

        // Dynamic state — THIS widening's own proof. Compared field-by-field (never via TransferState record
        // equality) because CaptureTransferState defensively copies its two arrays, so two logically-identical
        // captures never share array identity.
        var restoredState = restoredBody.CaptureTransferState();

        Assert.Equal(expected: capturedState.PlanarVelocity, actual: restoredState.PlanarVelocity);
        Assert.Equal(expected: capturedState.VerticalVelocity, actual: restoredState.VerticalVelocity);
        Assert.Equal(expected: capturedState.Orientation, actual: restoredState.Orientation);
        Assert.Equal(expected: capturedState.VehiclePitch, actual: restoredState.VehiclePitch);
        Assert.Equal(expected: capturedState.OverlayVelocity, actual: restoredState.OverlayVelocity);
        Assert.Equal(expected: capturedState.OverlayRemainingTicks, actual: restoredState.OverlayRemainingTicks);
        Assert.Equal(expected: capturedState.ChannelTimerTicks[1], actual: restoredState.ChannelTimerTicks[1]);
        Assert.Equal(expected: capturedState.ChannelTimerValues[1], actual: restoredState.ChannelTimerValues[1]);

        // THE OTHER PINNED INVARIANT — park never reinstates from a restore, even though this fixture's population
        // authors a positive reconnect grace (Fixtures.BuildDocumentCore's ReconnectGraceSeconds: 3.0f) that WOULD
        // park an ordinary DeactivateSeat leave. TryDetachSeatForTransfer/RestoreDetachedSeat never call
        // DeactivateSeat at all — park is a live-compiled-grace fact the NEXT deliberate leave re-derives, never a
        // snapshot this seam replays.
        Assert.False(condition: population.IsSeatParked(slot: actor.Index));
    }
}
