using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Authority laws for the live group-role projection. These cases exercise the engine's effective grant
/// surface only: a current local member's exact declared role may reach a group row, while a null/unknown role,
/// departed member, or member of another role may not. They do not claim consent, invitation, finder, or management
/// behavior.</summary>
public sealed class WorldGroupRoleAuthorityLawTests {
    [Fact]
    public void ExactRoleControlsEveryEffectivePayload_AndSameActorMayHoldDifferentGroupRoles() {
        var grants = NewGrants();
        var seat0 = WorldPrincipal.Seat(slot: 0);
        var seat1 = WorldPrincipal.Seat(slot: 1);
        var seat2 = WorldPrincipal.Seat(slot: 2);
        var body = GrantSubject.Body(index: 0);
        var state = GrantSubject.State(name: "facts");
        var section = GrantSubject.Section(section: WorldSection.State);
        var groups = new[] {
            Group(
                id: "readers",
                (seat0, "reader"),
                (seat1, "reader"),
                (seat2, null)
            ),
            Group(id: "drivers", (seat0, "driver")),
            Group(id: "writers", (seat0, "writer"))
        };

        grants.SyncGroups(groups: groups, kinds: [Kind()], ownership: []);

        var readGrant = new WorldGrant(
            Principal: WorldPrincipal.Group(id: "readers"),
            Capability: WorldCapability.Observe,
            Subject: body,
            Exclusive: false,
            Budget: 7,
            EventBudget: 5
        );
        var driveGrant = new WorldGrant(
            Principal: WorldPrincipal.Group(id: "drivers"),
            Capability: WorldCapability.Drive,
            Subject: body,
            Exclusive: false,
            Budget: 9,
            Reach: new ChannelReachMask(Bits: 1UL),
            HoldCeiling: 100L
        );
        var mutateSectionGrant = new WorldGrant(
            Principal: WorldPrincipal.Group(id: "writers"),
            Capability: WorldCapability.Mutate,
            Subject: section,
            Exclusive: false,
            Budget: 3,
            KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.State)
        );
        var mutateStateGrant = new WorldGrant(
            Principal: WorldPrincipal.Group(id: "writers"),
            Capability: WorldCapability.Mutate,
            Subject: state,
            Exclusive: false,
            WriteMask: DocumentWriteMask.All
        );

        AssertGrant(grants, readGrant);
        AssertGrant(grants, driveGrant);
        AssertGrant(grants, mutateSectionGrant);
        AssertGrant(grants, mutateStateGrant);

        Assert.Equal(expected: GrantRule.GroupHold, actual: grants.Allows(seat0, WorldCapability.Observe, body).Rule);
        Assert.Equal(expected: GrantRule.GroupHold, actual: grants.Allows(seat0, WorldCapability.Drive, body).Rule);
        Assert.Equal(expected: GrantRule.GroupHold, actual: grants.Allows(seat0, WorldCapability.Mutate, section).Rule);
        Assert.Equal(expected: GrantRule.GroupHold, actual: grants.Allows(seat0, WorldCapability.Mutate, state).Rule);
        Assert.Equal(expected: GrantRule.GroupHold, actual: grants.Allows(seat1, WorldCapability.Observe, body).Rule);
        Assert.Equal(expected: GrantRule.NoHold, actual: grants.Allows(seat1, WorldCapability.Drive, body).Rule);
        Assert.Equal(expected: GrantRule.NoHold, actual: grants.Allows(seat2, WorldCapability.Observe, body).Rule);

        Assert.True(grants.TryGetBudget(seat0, WorldCapability.Observe, body, out var observeBudget));
        Assert.Equal(expected: (ushort)7, actual: observeBudget);
        Assert.True(grants.TryGetEventBudget(seat0, WorldCapability.Observe, body, out var eventBudget));
        Assert.Equal(expected: (ushort)5, actual: eventBudget);
        Assert.True(grants.TryGetBudget(seat0, WorldCapability.Drive, body, out var driveBudget));
        Assert.Equal(expected: (ushort)9, actual: driveBudget);
        Assert.True(grants.TryGetChannelReach(seat0, body, out var reach));
        Assert.Equal(expected: 1UL, actual: reach.Bits);
        Assert.Equal(expected: 100L, actual: grants.HoldCeiling(seat0, body));
        Assert.True(grants.TryGetBudget(seat0, WorldCapability.Mutate, section, out var mutateBudget));
        Assert.Equal(expected: (ushort)3, actual: mutateBudget);
        Assert.True(grants.TryGetKindMask(seat0, WorldCapability.Mutate, section, out var kindMask));
        Assert.Equal(expected: WorldMutationKindCatalog.KindsOf(section: WorldSection.State), actual: kindMask);
        Assert.True(grants.TryGetWriteMask(seat0, WorldCapability.Mutate, state, out var writeMask));
        Assert.Equal(expected: DocumentWriteMask.All, actual: writeMask);
        Assert.Contains(expected: body, collection: grants.ProjectSubjects(seat0, WorldCapability.Observe));
    }

    [Fact]
    public void LeaveRoleChangeAndGrantRevokeTakeEffectImmediately_AndProjectionRevisionAdvances() {
        var grants = NewGrants();
        var seat = WorldPrincipal.Seat(slot: 0);
        var body = GrantSubject.Body(index: 0);
        var reader = Group(id: "readers", (seat, "reader"));

        grants.SyncGroups(groups: [reader], kinds: [Kind()], ownership: []);
        AssertGrant(grants, new WorldGrant(WorldPrincipal.Group("readers"), WorldCapability.Observe, body, false, Budget: 1));
        Assert.Equal(expected: GrantRule.GroupHold, actual: grants.Allows(seat, WorldCapability.Observe, body).Rule);
        var projectedRevision = grants.Revision;
        Assert.Contains(expected: body, collection: grants.ProjectSubjects(seat, WorldCapability.Observe));

        Assert.True(grants.Revoke(WorldPrincipal.Group("readers"), WorldCapability.Observe, body));
        Assert.Equal(expected: GrantRule.NoHold, actual: grants.Allows(seat, WorldCapability.Observe, body).Rule);

        grants.SyncGroups(groups: [reader with { Members = [new WorldGroupMember(WorldMemberRef.Local(seat), null, 1)], Revision = 1, NextJoinOrdinal = 2 }], kinds: [Kind()], ownership: []);
        Assert.True(grants.Revision > projectedRevision);
        Assert.Empty(grants.ProjectSubjects(seat, WorldCapability.Observe));

        grants.SyncGroups(groups: [], kinds: [Kind()], ownership: []);
        Assert.Equal(expected: GrantRule.NoHold, actual: grants.Allows(seat, WorldCapability.Observe, body).Rule);
    }

    [Fact]
    public void GroupOwnedReachUsesOwnerGroupRole_DirectPrincipalOwnershipRemainsSeparate() {
        var grants = NewGrants();
        var reader = WorldPrincipal.Seat(slot: 0);
        var controller = WorldPrincipal.Seat(slot: 1);
        var subjectMember = WorldPrincipal.Seat(slot: 2);
        var unrelated = WorldPrincipal.Seat(slot: 3);
        var screen0 = GrantSubject.Screen(index: 0);
        var screen1 = GrantSubject.Screen(index: 1);
        var owner = Group(id: "owners", (reader, "reader"), (controller, "controller"));
        var subject = Group(id: "subject", (subjectMember, "controller"));
        var direct = Group(id: "direct", (unrelated, "reader"));
        var ownership = new[] {
            new WorldOwnership(
                Subject: new OwnershipSubject(OwnershipSubjectKind.Group, "subject"),
                Owner: new OwnershipOwner(OwnershipOwnerKind.Group, GroupId: "owners")
            ),
            new WorldOwnership(
                Subject: new OwnershipSubject(OwnershipSubjectKind.Group, "direct"),
                Owner: new OwnershipOwner(OwnershipOwnerKind.Principal, Principal: reader)
            )
        };

        grants.SyncGroups(groups: [owner, subject, direct], kinds: [Kind()], ownership: ownership);
        AssertGrant(grants, new WorldGrant(WorldPrincipal.Group("subject"), WorldCapability.Control, screen0, false));
        AssertGrant(grants, new WorldGrant(WorldPrincipal.Group("direct"), WorldCapability.Control, screen1, false));

        Assert.Equal(expected: GrantRule.NoHold, actual: grants.Allows(reader, WorldCapability.Control, screen0).Rule);
        Assert.Equal(expected: GrantRule.OwnershipHold, actual: grants.Allows(controller, WorldCapability.Control, screen0).Rule);
        Assert.Equal(expected: GrantRule.OwnershipHold, actual: grants.Allows(reader, WorldCapability.Control, screen1).Rule);
        Assert.Equal(expected: GrantRule.NoHold, actual: grants.Allows(unrelated, WorldCapability.Control, screen0).Rule);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreStartsDenyByDefault_ThenRehydratesRoleProjectionFromCurrentDefinition(bool reusePopulatedTable) {
        var groups = new[] {
            Group(id: "readers", (WorldPrincipal.Seat(0), "reader")),
            Group(id: "owners", (WorldPrincipal.Seat(1), "controller")),
            Group(id: "subject", (WorldPrincipal.Seat(2), "reader"))
        };
        var ownership = new[] {
            new WorldOwnership(new OwnershipSubject(OwnershipSubjectKind.Group, "subject"),
                new OwnershipOwner(OwnershipOwnerKind.Group, GroupId: "owners"))
        };
        var controller = WorldPrincipal.Seat(1);
        var screen = GrantSubject.Screen(0);
        var kinds = new[] { Kind() };
        var principal = WorldPrincipal.Seat(slot: 0);
        var body = GrantSubject.Body(index: 0);
        var grants = NewGrants();
        grants.SyncGroups(groups: groups, kinds: kinds, ownership: ownership);
        AssertGrant(grants, new WorldGrant(WorldPrincipal.Group("readers"), WorldCapability.Observe, body, false, Budget: 1));
        Assert.Equal(expected: GrantRule.GroupHold, actual: grants.Allows(principal, WorldCapability.Observe, body).Rule);

        AssertGrant(grants, new WorldGrant(WorldPrincipal.Group("subject"), WorldCapability.Control, screen, false));
        Assert.Equal(GrantRule.OwnershipHold, grants.Allows(controller, WorldCapability.Control, screen).Rule);

        var checkpoint = grants.Capture();
        var restored = reusePopulatedTable ? grants : NewGrants();
        restored.Restore(checkpoint: checkpoint);
        Assert.Equal(expected: GrantRule.NoHold, actual: restored.Allows(principal, WorldCapability.Observe, body).Rule);
        Assert.Empty(restored.ProjectSubjects(principal, WorldCapability.Observe));

        Assert.Equal(GrantRule.NoHold, restored.Allows(controller, WorldCapability.Control, screen).Rule);
        restored.SyncGroups(groups: groups, kinds: kinds, ownership: ownership);
        Assert.Equal(GrantRule.OwnershipHold, restored.Allows(controller, WorldCapability.Control, screen).Rule);
        Assert.Equal(expected: GrantRule.GroupHold, actual: restored.Allows(principal, WorldCapability.Observe, body).Rule);
        Assert.True(restored.TryGetBudget(principal, WorldCapability.Observe, body, out var budget));
        Assert.Equal(expected: (ushort)1, actual: budget);
    }

    private static WorldGrants NewGrants() => new(
        seatCount: 0,
        population: 4,
        routeTransition: static (_, _, _) => { }
    );

    private static WorldGroupKind Kind() => new(
        Name: "authority",
        Roles: [
            new WorldGroupRole(Name: "reader", Capabilities: [WorldCapability.Observe]),
            new WorldGroupRole(Name: "driver", Capabilities: [WorldCapability.Drive]),
            new WorldGroupRole(Name: "writer", Capabilities: [WorldCapability.Mutate]),
            new WorldGroupRole(Name: "controller", Capabilities: [WorldCapability.Control])
        ],
        Lifetime: WorldGroupLifetime.Persistent,
        EvictionPolicy: WorldGroupEvictionPolicy.Remove,
        Capacity: 8
    );

    private static WorldGroup Group(string id, params (WorldPrincipal Principal, string? Role)[] members) => new(
        Id: SafeName.Parse(candidate: id),
        KindName: "authority",
        Members: [.. members.Select(static member => new WorldGroupMember(
            Ref: WorldMemberRef.Local(principal: member.Principal),
            Role: member.Role,
            JoinOrdinal: 0
        ))],
        NextJoinOrdinal: members.Length
    );

    private static void AssertGrant(WorldGrants grants, WorldGrant grant) {
        Assert.True(grants.TryGrant(grant: grant, reason: out var reason), userMessage: reason);
    }
}
