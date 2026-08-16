using Xunit;

using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldBody.FixedPreviousPosition"/> — the swept portal-crossing scan's segment
/// start point. <c>Puck.World</c> (the composition root, home of <c>WorldInstanceHost.ScanPortalFace</c> and the
/// swept slab clip itself) is out of reach for this project (see README.md/Fixtures.cs), so this suite proves the
/// PRIMITIVE the scan depends on rather than the scan itself: <c>WorldBody.Advance</c> captures the pre-step
/// origin at the top, before any movement, and a hard teleport (<see cref="WorldBody.Pose(FixedVector3, Puck.Maths.FixedQ4816, Puck.Maths.FixedQ4816, Puck.Maths.FixedQ4816)"/>,
/// <see cref="WorldBody.Reconcile"/>) resets it to the landing position rather than leaving a ghost segment behind.
/// The swept-crossing matrix itself (ordinary walk / tunnelling / warp-through-and-beyond / warp-into) is verified
/// by RUNNING <c>Puck.World</c> (CLAUDE.md rule 3 — game features are not gated).
/// </summary>
public sealed class PortalSweepOriginLawTests {
    [Fact]
    public void Advance_CapturesPreStepOrigin_NotPostStepOrigin() {
        using var fixture = Fixtures.FreshServer();
        var actor = WorldPrincipal.Seat(slot: 0);

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey));

        var body = fixture.Server.Body(index: actor.Index)!;
        var spawnPosition = body.FixedPosition;

        // The fixture's kit carries no collider (Fixtures.BuildDocument), so gravity alone moves the body every
        // step from tick 1 on — a real, non-degenerate previous/current pair with no player intent required.
        fixture.Step();

        Assert.Equal(expected: spawnPosition, actual: body.FixedPreviousPosition);
        Assert.NotEqual(expected: spawnPosition, actual: body.FixedPosition);

        var afterFirstStep = body.FixedPosition;

        fixture.Step();

        // The captured origin tracks EACH step's own pre-step position — not stuck at spawn forever.
        Assert.Equal(expected: afterFirstStep, actual: body.FixedPreviousPosition);
        Assert.NotEqual(expected: afterFirstStep, actual: body.FixedPosition);
    }
    [Fact]
    public void Pose_HardTeleport_CollapsesSweptSegmentToLandingPosition() {
        using var fixture = Fixtures.FreshServer();
        var actor = WorldPrincipal.Seat(slot: 0);

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey));

        var body = fixture.Server.Body(index: actor.Index)!;

        // Establish a REAL non-degenerate segment first (falling under gravity) — the discriminating case a warp
        // must suppress; without it, previous already equalling current would prove nothing about the reset.
        fixture.Step();
        fixture.Step();

        Assert.NotEqual(expected: body.FixedPreviousPosition, actual: body.FixedPosition);

        var landing = new FixedVector3(X: FixedQ4816.FromDouble(value: 5.0), Y: FixedQ4816.FromDouble(value: 10.0), Z: FixedQ4816.FromDouble(value: -3.0));

        body.Pose(position: landing, yawRadians: FixedQ4816.Zero, pitchRadians: FixedQ4816.Zero, rollRadians: FixedQ4816.Zero);

        Assert.Equal(expected: landing, actual: body.FixedPosition);
        // THE ruled behavior: a hard teleport resets the swept segment's start to the landing spot, so a portal
        // scan immediately after sees a degenerate point at the NEW position — never a ghost segment from where the
        // body used to be.
        Assert.Equal(expected: landing, actual: body.FixedPreviousPosition);
    }
    [Fact]
    public void Reconcile_HardCorrection_CollapsesSweptSegmentToLandingPosition() {
        using var fixture = Fixtures.FreshServer();
        var actor = WorldPrincipal.Seat(slot: 0);

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey));

        var body = fixture.Server.Body(index: actor.Index)!;

        fixture.Step();
        fixture.Step();

        Assert.NotEqual(expected: body.FixedPreviousPosition, actual: body.FixedPosition);

        _ = body.Reconcile(seconds: 0.25f, x: 7f, yawRadians: 0f, z: 2f);

        // Reconcile is the OTHER hard-write site the brief names (CommitTeleport runs regardless of whether the
        // continuity kind it reports afterward is Teleport or Correction) — same reset, same reason.
        Assert.Equal(expected: body.FixedPosition, actual: body.FixedPreviousPosition);
    }
}
