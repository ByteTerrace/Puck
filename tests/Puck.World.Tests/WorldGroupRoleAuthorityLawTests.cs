using Puck.Commands;
using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Authority laws for the live group-role projection. These cases exercise the engine's effective grant
/// surface only: a current local member's exact declared role may reach a group row, while a null/unknown role,
/// departed member, or member of another role may not. They do not claim consent, invitation, finder, or management
/// behavior.</summary>
public sealed class WorldGroupRoleAuthorityLawTests {
    private static void AssertGrant(WorldGrants grants, WorldGrant grant) {
        Assert.True(
            grants.TryGrant(
                grant: grant,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
    private static WorldGroup Group(string id, params (Principal Principal, string? Role)[] members) => new(
        Id: SafeName.Parse(candidate: id),
        KindName: "authority",
        Members: [.. members.Select(selector: static member => new WorldGroupMember(
                Ref: WorldMemberRef.Local(principal: member.Principal),
                Role: member.Role,
                JoinOrdinal: 0
            ))],
        NextJoinOrdinal: members.Length
    );
    private static WorldGroupKind Kind() => new(
        Name: "authority",
        Roles: [
            new WorldGroupRole(
                Capabilities: [WorldCapability.Observe],
                Name: "reader"
            ),
            new WorldGroupRole(
                Capabilities: [WorldCapability.Drive],
                Name: "driver"
            ),
            new WorldGroupRole(
                Capabilities: [WorldCapability.Mutate],
                Name: "writer"
            ),
            new WorldGroupRole(
                Capabilities: [WorldCapability.Control],
                Name: "controller"
            )
        ],
        Lifetime: WorldGroupLifetime.Persistent,
        EvictionPolicy: WorldGroupEvictionPolicy.Remove,
        Capacity: 8
    );
    private static WorldGrants NewGrants() => new(
        population: 4,
        routeTransition: static (_, _, _) => { },
        seatCount: 0
    );

    [Fact]
    public void ExactRoleControlsEveryEffectivePayload_AndSameActorMayHoldDifferentGroupRoles() {
        var grants = NewGrants();
        var seat0 = Principal.Seat(slot: 0);
        var seat1 = Principal.Seat(slot: 1);
        var seat2 = Principal.Seat(slot: 2);
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
            Group(
            id: "drivers",
            (seat0, "driver")
        ),
            Group(
            id: "writers",
            (seat0, "writer")
        ),
        };

        grants.SyncGroups(
            groups: groups,
            kinds: [Kind()],
            ownership: []
        );

        var readGrant = new WorldGrant(
            Grantee: Grantee.Group(id: "readers"),
            Capability: WorldCapability.Observe,
            Subject: body,
            Exclusive: false,
            Budget: 7,
            EventBudget: 5
        );
        var driveGrant = new WorldGrant(
            Grantee: Grantee.Group(id: "drivers"),
            Capability: WorldCapability.Drive,
            Subject: body,
            Exclusive: false,
            Budget: 9,
            Reach: new ChannelReachMask(Bits: 1UL),
            HoldCeiling: 100L
        );
        var mutateSectionGrant = new WorldGrant(
            Grantee: Grantee.Group(id: "writers"),
            Capability: WorldCapability.Mutate,
            Subject: section,
            Exclusive: false,
            Budget: 3,
            KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.State)
        );
        var mutateStateGrant = new WorldGrant(
            Grantee: Grantee.Group(id: "writers"),
            Capability: WorldCapability.Mutate,
            Subject: state,
            Exclusive: false,
            WriteMask: DocumentWriteMask.All
        );

        AssertGrant(
            grant: readGrant,
            grants: grants
        );
        AssertGrant(
            grant: driveGrant,
            grants: grants
        );
        AssertGrant(
            grant: mutateSectionGrant,
            grants: grants
        );
        AssertGrant(
            grant: mutateStateGrant,
            grants: grants
        );

        Assert.Equal(
            expected: GrantRule.GroupHold,
            actual: grants.Allows(
                capability: WorldCapability.Observe,
                principal: seat0,
                subject: body
            ).Rule
        );
        Assert.Equal(
            expected: GrantRule.GroupHold,
            actual: grants.Allows(
                capability: WorldCapability.Drive,
                principal: seat0,
                subject: body
            ).Rule
        );
        Assert.Equal(
            expected: GrantRule.GroupHold,
            actual: grants.Allows(
                capability: WorldCapability.Mutate,
                principal: seat0,
                subject: section
            ).Rule
        );
        Assert.Equal(
            expected: GrantRule.GroupHold,
            actual: grants.Allows(
                capability: WorldCapability.Mutate,
                principal: seat0,
                subject: state
            ).Rule
        );
        Assert.Equal(
            expected: GrantRule.GroupHold,
            actual: grants.Allows(
                capability: WorldCapability.Observe,
                principal: seat1,
                subject: body
            ).Rule
        );
        Assert.Equal(
            expected: GrantRule.NoHold,
            actual: grants.Allows(
                capability: WorldCapability.Drive,
                principal: seat1,
                subject: body
            ).Rule
        );
        Assert.Equal(
            expected: GrantRule.NoHold,
            actual: grants.Allows(
                capability: WorldCapability.Observe,
                principal: seat2,
                subject: body
            ).Rule
        );

        Assert.True(condition: grants.TryGetBudget(
            budget: out var observeBudget,
            capability: WorldCapability.Observe,
            principal: seat0,
            subject: body
        ));
        Assert.Equal(
            actual: observeBudget,
            expected: ((ushort)7)
        );
        Assert.True(condition: grants.TryGetEventBudget(
            budget: out var eventBudget,
            capability: WorldCapability.Observe,
            principal: seat0,
            subject: body
        ));
        Assert.Equal(
            actual: eventBudget,
            expected: ((ushort)5)
        );
        Assert.True(condition: grants.TryGetBudget(
            budget: out var driveBudget,
            capability: WorldCapability.Drive,
            principal: seat0,
            subject: body
        ));
        Assert.Equal(
            actual: driveBudget,
            expected: ((ushort)9)
        );
        Assert.True(condition: grants.TryGetChannelReach(
            mask: out var reach,
            principal: seat0,
            subject: body
        ));
        Assert.Equal(
            expected: 1UL,
            actual: reach.Bits
        );
        Assert.Equal(
            expected: 100L,
            actual: grants.HoldCeiling(
                grantee: seat0,
                subject: body
            )
        );
        Assert.True(condition: grants.TryGetBudget(
            budget: out var mutateBudget,
            capability: WorldCapability.Mutate,
            principal: seat0,
            subject: section
        ));
        Assert.Equal(
            actual: mutateBudget,
            expected: ((ushort)3)
        );
        Assert.True(condition: grants.TryGetKindMask(
            capability: WorldCapability.Mutate,
            grantee: seat0,
            mask: out var kindMask,
            subject: section
        ));
        Assert.Equal(
            expected: WorldMutationKindCatalog.KindsOf(section: WorldSection.State),
            actual: kindMask
        );
        Assert.True(condition: grants.TryGetWriteMask(
            capability: WorldCapability.Mutate,
            grantee: seat0,
            mask: out var writeMask,
            subject: state
        ));
        Assert.Equal(
            expected: DocumentWriteMask.All,
            actual: writeMask
        );
        Assert.Contains(
            expected: body,
            collection: grants.ProjectSubjects(
                capability: WorldCapability.Observe,
                principal: seat0
            )
        );
    }
    [Fact]
    public void GroupOwnedReachUsesOwnerGroupRole_DirectPrincipalOwnershipRemainsSeparate() {
        var grants = NewGrants();
        var reader = Principal.Seat(slot: 0);
        var controller = Principal.Seat(slot: 1);
        var subjectMember = Principal.Seat(slot: 2);
        var unrelated = Principal.Seat(slot: 3);
        var screen0 = GrantSubject.Screen(index: 0);
        var screen1 = GrantSubject.Screen(index: 1);
        var owner = Group(
            id: "owners",
            (reader, "reader"),
            (controller, "controller")
        );
        var subject = Group(
            id: "subject",
            (subjectMember, "controller")
        );
        var direct = Group(
            id: "direct",
            (unrelated, "reader")
        );
        var ownership = new[] {
            new WorldOwnership(
            Subject: new OwnershipSubject(
                Id: "subject",
                Kind: OwnershipSubjectKind.Group
            ),
            Owner: new OwnershipOwner(
                OwnershipOwnerKind.Group,
                GroupId: "owners"
            )
        ),
            new WorldOwnership(
            Subject: new OwnershipSubject(
                Id: "direct",
                Kind: OwnershipSubjectKind.Group
            ),
            Owner: new OwnershipOwner(
                OwnershipOwnerKind.Principal,
                Principal: reader
            )
        ),
        };

        grants.SyncGroups(
            groups: [owner, subject, direct],
            kinds: [Kind()],
            ownership: ownership
        );
        AssertGrant(
            grants,
            new WorldGrant(
                Grantee.Group(id: "subject"),
                WorldCapability.Control,
                screen0,
                false
            )
        );
        AssertGrant(
            grants,
            new WorldGrant(
                Grantee.Group(id: "direct"),
                WorldCapability.Control,
                screen1,
                false
            )
        );

        Assert.Equal(
            expected: GrantRule.NoHold,
            actual: grants.Allows(
                capability: WorldCapability.Control,
                principal: reader,
                subject: screen0
            ).Rule
        );
        Assert.Equal(
            expected: GrantRule.OwnershipHold,
            actual: grants.Allows(
                capability: WorldCapability.Control,
                principal: controller,
                subject: screen0
            ).Rule
        );
        Assert.Equal(
            expected: GrantRule.OwnershipHold,
            actual: grants.Allows(
                capability: WorldCapability.Control,
                principal: reader,
                subject: screen1
            ).Rule
        );
        Assert.Equal(
            expected: GrantRule.NoHold,
            actual: grants.Allows(
                capability: WorldCapability.Control,
                principal: unrelated,
                subject: screen0
            ).Rule
        );
    }
    [Fact]
    public void LeaveRoleChangeAndGrantRevokeTakeEffectImmediately_AndProjectionRevisionAdvances() {
        var grants = NewGrants();
        var seat = Principal.Seat(slot: 0);
        var body = GrantSubject.Body(index: 0);
        var reader = Group(
            id: "readers",
            (seat, "reader")
        );

        grants.SyncGroups(
            groups: [reader],
            kinds: [Kind()],
            ownership: []
        );
        AssertGrant(
            grants,
            new WorldGrant(
                Grantee.Group(id: "readers"),
                WorldCapability.Observe,
                body,
                false,
                Budget: 1
            )
        );
        Assert.Equal(
            expected: GrantRule.GroupHold,
            actual: grants.Allows(
                capability: WorldCapability.Observe,
                principal: seat,
                subject: body
            ).Rule
        );
        var projectedRevision = grants.Revision;

        Assert.Contains(
            expected: body,
            collection: grants.ProjectSubjects(
                capability: WorldCapability.Observe,
                principal: seat
            )
        );

        Assert.True(condition: grants.Revoke(
            Grantee.Group(id: "readers"),
            WorldCapability.Observe,
            body
        ));
        Assert.Equal(
            expected: GrantRule.NoHold,
            actual: grants.Allows(
                capability: WorldCapability.Observe,
                principal: seat,
                subject: body
            ).Rule
        );

        grants.SyncGroups(
            groups: [reader with { Members = [new WorldGroupMember(
                        WorldMemberRef.Local(principal: seat),
                        null,
                        1
                    )], Revision = 1, NextJoinOrdinal = 2 }],
            kinds: [Kind()],
            ownership: []
        );
        Assert.True(condition: (grants.Revision > projectedRevision));
        Assert.Empty(collection: grants.ProjectSubjects(
            capability: WorldCapability.Observe,
            principal: seat
        ));

        grants.SyncGroups(
            groups: [],
            kinds: [Kind()],
            ownership: []
        );
        Assert.Equal(
            expected: GrantRule.NoHold,
            actual: grants.Allows(
                capability: WorldCapability.Observe,
                principal: seat,
                subject: body
            ).Rule
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void RestoreStartsDenyByDefault_ThenRehydratesRoleProjectionFromCurrentDefinition(bool reusePopulatedTable) {
        var groups = new[] {
            Group(
            id: "readers",
            (Principal.Seat(slot: 0), "reader")
        ),
            Group(
            id: "owners",
            (Principal.Seat(slot: 1), "controller")
        ),
            Group(
            id: "subject",
            (Principal.Seat(slot: 2), "reader")
        ),
        };
        var ownership = new[] {
            new WorldOwnership(
            new OwnershipSubject(
                Id: "subject",
                Kind: OwnershipSubjectKind.Group
            ),
            new OwnershipOwner(
                OwnershipOwnerKind.Group,
                GroupId: "owners"
            )
        ),
        };
        var controller = Principal.Seat(slot: 1);
        var screen = GrantSubject.Screen(index: 0);
        var kinds = new[] { Kind() };
        var principal = Principal.Seat(slot: 0);
        var body = GrantSubject.Body(index: 0);
        var grants = NewGrants();

        grants.SyncGroups(
            groups: groups,
            kinds: kinds,
            ownership: ownership
        );
        AssertGrant(
            grants,
            new WorldGrant(
                Grantee.Group(id: "readers"),
                WorldCapability.Observe,
                body,
                false,
                Budget: 1
            )
        );
        Assert.Equal(
            expected: GrantRule.GroupHold,
            actual: grants.Allows(
                capability: WorldCapability.Observe,
                principal: principal,
                subject: body
            ).Rule
        );

        AssertGrant(
            grants,
            new WorldGrant(
                Grantee.Group(id: "subject"),
                WorldCapability.Control,
                screen,
                false
            )
        );
        Assert.Equal(
            GrantRule.OwnershipHold,
            grants.Allows(
                capability: WorldCapability.Control,
                principal: controller,
                subject: screen
            ).Rule
        );

        var checkpoint = grants.Capture();
        var restored = (reusePopulatedTable
            ? grants
            : NewGrants()
        );

        restored.Restore(checkpoint: checkpoint);
        Assert.Equal(
            expected: GrantRule.NoHold,
            actual: restored.Allows(
                capability: WorldCapability.Observe,
                principal: principal,
                subject: body
            ).Rule
        );
        Assert.Empty(collection: restored.ProjectSubjects(
            capability: WorldCapability.Observe,
            principal: principal
        ));

        Assert.Equal(
            GrantRule.NoHold,
            restored.Allows(
                capability: WorldCapability.Control,
                principal: controller,
                subject: screen
            ).Rule
        );
        restored.SyncGroups(
            groups: groups,
            kinds: kinds,
            ownership: ownership
        );
        Assert.Equal(
            GrantRule.OwnershipHold,
            restored.Allows(
                capability: WorldCapability.Control,
                principal: controller,
                subject: screen
            ).Rule
        );
        Assert.Equal(
            expected: GrantRule.GroupHold,
            actual: restored.Allows(
                capability: WorldCapability.Observe,
                principal: principal,
                subject: body
            ).Rule
        );
        Assert.True(condition: restored.TryGetBudget(
            budget: out var budget,
            capability: WorldCapability.Observe,
            principal: principal,
            subject: body
        ));
        Assert.Equal(
            actual: budget,
            expected: ((ushort)1)
        );
    }
}
