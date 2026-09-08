using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// The control-application set laws — the primitive engagement IS, rather than a route triple beside a latch.
/// <list type="bullet">
/// <item><description>An uncomposed participant holds exactly its own-body application, so its avatar drives
/// itself.</description></item>
/// <item><description>Capture is that member's ABSENCE: an exclusive composition removes it and the capture latch
/// (<c>WorldBody.Engaged</c>, a derived projection) reads true; a mirrored composition retains it and the latch reads
/// false. There is no second storage to disagree with.</description></item>
/// <item><description>Dissolving restores the default, and the latch follows in the same operation.</description></item>
/// <item><description>Revoking Control over an applied target dissolves that member — the state a repair machine
/// used to have to detect after the fact is unreachable.</description></item>
/// <item><description>Two seats applied to one screen merge into ONE pad (the multiplayer-cabinet OR-merge).</description></item>
/// </list>
/// </summary>
public sealed class ControlApplicationLawTests {
    private static WorldPrincipal Join(WorldFixture fixture, int slot) {
        var principal = WorldPrincipal.Seat(slot: slot);

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: principal,
            Slot: slot,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));

        return principal;
    }

    [Fact]
    public void UncomposedParticipantHoldsOnlyItsOwnBodyApplication() {
        using var fixture = Fixtures.FreshServer();

        var seat = Join(
            fixture: fixture,
            slot: 0
        );
        var applications = fixture.Server.Grants.Applications(principal: seat);

        Assert.Equal(
            expected: ControlApplication.OwnBody(bodyIndex: seat.Index),
            actual: Assert.Single(collection: applications)
        );
        Assert.False(condition: fixture.Server.Body(index: seat.Index)!.Engaged);
    }
    [Fact]
    public void ExclusiveCompositionDropsTheOwnBodyMember_MirroredCompositionRetainsIt() {
        using var fixture = Fixtures.FreshServer();

        var seat = Join(
            fixture: fixture,
            slot: 0
        );
        var own = GrantSubject.Body(index: seat.Index);
        var target = GrantSubject.Screen(index: Fixtures.TestPatternScreenIndex);

        Assert.True(condition: fixture.Server.Engagement.Compose(
            entityIndex: seat.Index,
            target: target,
            exclusive: true,
            actingPrincipal: seat,
            targetPrincipal: seat
        ));

        var captured = fixture.Server.Grants.Applications(principal: seat);

        Assert.Equal(
            expected: target,
            actual: Assert.Single(collection: captured).Target
        );
        Assert.True(condition: fixture.Server.Body(index: seat.Index)!.Engaged);

        // The SAME target, composed mirrored — the one discriminating fact is `exclusive`, and it moves BOTH the set
        // membership and the derived latch together.
        Assert.True(condition: fixture.Server.Engagement.Compose(
            entityIndex: seat.Index,
            target: target,
            exclusive: false,
            actingPrincipal: seat,
            targetPrincipal: seat
        ));

        var mirrored = fixture.Server.Grants.Applications(principal: seat);

        Assert.Equal(
            expected: 2,
            actual: mirrored.Count
        );
        Assert.Contains(
            collection: mirrored,
            filter: application => (application.Target == own)
        );
        Assert.Contains(
            collection: mirrored,
            filter: application => (application.Target == target)
        );
        Assert.False(condition: fixture.Server.Body(index: seat.Index)!.Engaged);
    }
    [Fact]
    public void DissolveRestoresTheDefaultSetAndReleasesTheLatch() {
        using var fixture = Fixtures.FreshServer();

        var seat = Join(
            fixture: fixture,
            slot: 0
        );
        var target = GrantSubject.Screen(index: Fixtures.TestPatternScreenIndex);

        Assert.True(condition: fixture.Server.Engagement.Compose(
            entityIndex: seat.Index,
            target: target,
            exclusive: true,
            actingPrincipal: seat,
            targetPrincipal: seat
        ));
        Assert.Equal(
            expected: ControlOutcome.Dissolved,
            actual: fixture.Server.Engagement.Dissolve(
                entityIndex: seat.Index,
                actingPrincipal: seat,
                targetPrincipal: seat
            )
        );
        Assert.Equal(
            expected: ControlApplication.OwnBody(bodyIndex: seat.Index),
            actual: Assert.Single(collection: fixture.Server.Grants.Applications(principal: seat))
        );
        Assert.False(condition: fixture.Server.Body(index: seat.Index)!.Engaged);

        // A set already at its default is a friendly no-op, never a denial and never a repair.
        Assert.Equal(
            expected: ControlOutcome.NotApplied,
            actual: fixture.Server.Engagement.Dissolve(
                entityIndex: seat.Index,
                actingPrincipal: seat,
                targetPrincipal: seat
            )
        );
    }
    [Fact]
    public void RevokingControlOverAnAppliedTargetDissolvesThatMember() {
        using var fixture = Fixtures.FreshServer();

        var seat = Join(
            fixture: fixture,
            slot: 0
        );
        var target = GrantSubject.Screen(index: Fixtures.TestPatternScreenIndex);

        Assert.True(condition: fixture.Server.Engagement.Compose(
            entityIndex: seat.Index,
            target: target,
            exclusive: true,
            actingPrincipal: seat,
            targetPrincipal: seat
        ));
        Assert.True(condition: fixture.Server.Body(index: seat.Index)!.Engaged);

        // Stripping the authority that admitted the application takes the application with it — the desync a
        // route-without-latch/latch-without-route repair existed to detect cannot arise.
        fixture.Server.Revoke(
            grant: new WorldGrant(
                Principal: seat,
                Capability: WorldCapability.Control,
                Subject: GrantSubject.All,
                Exclusive: false
            ),
            actor: WorldPrincipal.Console
        );

        Assert.Equal(
            expected: ControlApplication.OwnBody(bodyIndex: seat.Index),
            actual: Assert.Single(collection: fixture.Server.Grants.Applications(principal: seat))
        );
    }
    [Fact]
    public void TwoSeatsAppliedToOneScreenMergeIntoOnePad() {
        using var fixture = Fixtures.FreshServer();

        var first = Join(
            fixture: fixture,
            slot: 0
        );
        var second = Join(
            fixture: fixture,
            slot: 1
        );
        var target = GrantSubject.Screen(index: Fixtures.TestPatternScreenIndex);

        foreach (var seat in new[] { first, second }) {
            Assert.True(condition: fixture.Server.Engagement.Compose(
                entityIndex: seat.Index,
                target: target,
                exclusive: true,
                actingPrincipal: seat,
                targetPrincipal: seat
            ));
        }

        fixture.Server.Engagement.FoldTick();

        var pads = fixture.Server.Engagement.BuildPadSnapshot().Span;

        Assert.Equal(
            expected: 1,
            actual: pads.Length
        );
        Assert.Equal(
            expected: Fixtures.TestPatternScreenIndex,
            actual: pads[0].ScreenIndex
        );

        var occupants = fixture.Server.Engagement.PlayersOn(screenIndex: Fixtures.TestPatternScreenIndex);

        Assert.Equal(
            expected: 2,
            actual: occupants.Count
        );
        Assert.All(
            collection: occupants,
            action: static occupant => Assert.True(condition: occupant.Capture)
        );
    }
}
