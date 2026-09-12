using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Schema.Tests;

/// <summary>Round-1 laws for the local/verified membership value shape. Each refusal has a nearby passing control
/// that changes only the discriminating fact.</summary>
public sealed class WorldGroupMembershipLawTests {
    [Fact]
    public void Identity_UsesLengthPrefixedSegments_AndRoundTripsSeparators() {
        var identity = new WorldMemberIdentity(
            Issuer: "https://issuer.example/a:b~c",
            Subject: "user:1~2",
            World: SafeName.Parse(candidate: "arena")
        );

        var token = identity.Describe();

        Assert.True(condition: WorldMemberIdentity.TryParseCanonical(token: token, identity: out var parsed));
        Assert.Equal(expected: identity, actual: parsed);
        Assert.True(condition: identity.IsCanonical());
    }

    [Fact]
    public void Identity_NamesThatDifferOnlyAtASeparatorRemainInjective() {
        var first = new WorldMemberIdentity(Issuer: "ab", Subject: "c", World: null);
        var second = new WorldMemberIdentity(Issuer: "a", Subject: "bc", World: null);

        Assert.NotEqual(expected: first.Describe(), actual: second.Describe());
        Assert.True(condition: WorldMemberIdentity.TryParseCanonical(token: first.Describe(), identity: out _));
        Assert.True(condition: WorldMemberIdentity.TryParseCanonical(token: second.Describe(), identity: out _));
    }

    [Fact]
    public void Identity_NonCanonicalSpellingRefuses_AndCanonicalControlParses() {
        var identity = new WorldMemberIdentity(Issuer: "issuer", Subject: "subject", World: null);
        var nonCanonical = identity.Describe().Replace(oldValue: "member:", newValue: "MEMBER:", comparisonType: StringComparison.Ordinal);

        MembershipLawAssertions.RefusalWithControl(
            lawId: "world-member-identity.canonical-spelling",
            deniedOutcome: () => WorldMemberIdentity.TryParseCanonical(token: nonCanonical, identity: out _),
            controlOutcome: () => WorldMemberIdentity.TryParseCanonical(token: identity.Describe(), identity: out _)
        );
    }

    [Fact]
    public void Identity_MalformedLengthWorldAndControlInputRefuse_AndCanonicalControlPasses() {
        var identity = new WorldMemberIdentity(Issuer: "issuer", Subject: "subject", World: SafeName.Parse(candidate: "arena"));
        var canonical = identity.Describe();

        Assert.False(condition: WorldMemberIdentity.TryParseCanonical(token: "member:01~a7~subject", identity: out _));
        Assert.False(condition: WorldMemberIdentity.TryParseCanonical(token: "member:999999999999999999999~a1~b", identity: out _));
        Assert.False(condition: WorldMemberIdentity.TryParseCanonical(token: "member:2~a1~b", identity: out _));
        Assert.False(condition: WorldMemberIdentity.TryParseCanonical(token: $"{canonical}1~x", identity: out _));
        Assert.False(condition: new WorldMemberIdentity(Issuer: "issuer\0", Subject: "subject", World: null).IsCanonical());
        Assert.False(condition: new WorldMemberIdentity(Issuer: "issuer", Subject: "subject", World: default(SafeName)).IsCanonical());
        Assert.True(condition: WorldMemberIdentity.TryParseCanonical(token: canonical, identity: out _));
    }

    [Fact]
    public void MemberRef_LocalCannotSatisfyVerifiedArm() {
        var local = WorldMemberRef.Local(principal: WorldPrincipal.Seat(slot: 0));

        Assert.True(condition: local.IsCanonical());
        Assert.False(condition: WorldMemberRef.TryParseCanonical(token: $"verified:{local.Describe()}", member: out _));
        Assert.False(condition: WorldMemberRef.TryParseCanonical(token: "local:verified:member:6~issuer7~subject", member: out _));
    }

    [Fact]
    public void MemberRef_DefaultAndMismatchedPayloadsRefuse_AndCanonicalArmsPass() {
        var identity = new WorldMemberIdentity(Issuer: "issuer", Subject: "subject", World: null);
        var malformed = new WorldMemberRef(
            Kind: MemberRefKind.Local,
            Principal: null,
            Verified: identity
        );

        MembershipLawAssertions.RefusalWithControl(
            lawId: "world-member-ref.exact-union-arm",
            deniedOutcome: () => malformed.IsCanonical(),
            controlOutcome: () => WorldMemberRef.VerifiedIdentity(identity: identity).IsCanonical()
        );

        Assert.False(condition: default(WorldMemberRef).IsCanonical());
        Assert.False(condition: new WorldMemberRef(Kind: (MemberRefKind)99).IsCanonical());
    }

    [Fact]
    public void MembershipJson_RejectsUnknownNestedMembers() {
        var validJson = "{\"ref\":{\"kind\":\"Verified\",\"verified\":{\"issuer\":\"issuer\",\"subject\":\"subject\",\"world\":null}},\"role\":null,\"joinOrdinal\":0}";
        var json = validJson.Replace(oldValue: "\"world\":null}", newValue: "\"world\":null,\"extra\":\"x\"}", comparisonType: StringComparison.Ordinal);

        Assert.NotNull(System.Text.Json.JsonSerializer.Deserialize<WorldGroupMember>(validJson, WorldJsonContext.Default.Options));
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<WorldGroupMember>(json, WorldJsonContext.Default.Options));
    }

    [Fact]
    public void SocialKinds_FullDefinitionValidationAcceptsDistinctKindsAndRosters() {
        // These names and role labels are test vocabulary only: the engine ships no social-kind catalog or finder.
        // This law proves that one complete authored groups section can carry the intended relationship shapes.
        var definition = SocialDefinition();

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason),
            userMessage: reason
        );

        var groups = definition.Groups!;
        Assert.Equal(expected: 5, actual: groups.Kinds.Count);
        Assert.Equal(expected: 6, actual: groups.Groups.Count);
        Assert.Equal(expected: groups.Groups[0].Members[0].Ref, actual: groups.Groups[1].Members[0].Ref);
        Assert.Equal(expected: "friend", actual: groups.Groups[0].Members[0].Role);
        Assert.Equal(expected: "friend", actual: groups.Groups[1].Members[0].Role);
        Assert.NotEqual(expected: groups.Groups[0].Members[0].Ref, actual: groups.Groups[2].Members[0].Ref);
        Assert.Null(@object: groups.Groups[0].Members[1].Role);
    }

    [Fact]
    public void SocialKinds_FullDefinitionValidationRefusesUndeclaredRoleAndOverCapacity() {
        var definition = SocialDefinition();
        var groups = definition.Groups!;

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var validReason),
            userMessage: validReason
        );

        var wrongRole = groups.Groups[0] with {
            Members = [groups.Groups[0].Members[0] with { Role = "not-declared" }, groups.Groups[0].Members[1]]
        };
        var wrongRoleGroups = groups.Groups.ToArray();
        wrongRoleGroups[0] = wrongRole;
        var wrongRoleDefinition = definition with { Groups = groups with { Groups = wrongRoleGroups } };

        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: wrongRoleDefinition, reason: out var roleReason)
        );
        Assert.Contains(expectedSubstring: "names no role declared", actualString: roleReason);

        var friendship = groups.Groups[0];
        var overCapacity = friendship with {
            Members = [
                ..friendship.Members,
                new WorldGroupMember(
                    Ref: WorldMemberRef.Local(principal: WorldPrincipal.Seat(slot: 2)),
                    Role: "friend",
                    JoinOrdinal: 2
                )
            ],
            NextJoinOrdinal = 3
        };
        var overCapacityGroups = groups.Groups.ToArray();
        overCapacityGroups[0] = overCapacity;
        var overCapacityDefinition = definition with { Groups = groups with { Groups = overCapacityGroups } };

        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: overCapacityDefinition, reason: out var capacityReason)
        );
        Assert.Contains(expectedSubstring: "exceeding capacity", actualString: capacityReason);
    }

    private static WorldDefinition SocialDefinition() {
        var identity = new WorldMemberIdentity(
            Issuer: "issuer.example",
            Subject: "subject-42",
            World: SafeName.Parse(candidate: "social-world")
        );
        var qualified = WorldMemberRef.VerifiedIdentity(identity: identity);
        var seat0 = WorldMemberRef.Local(principal: WorldPrincipal.Seat(slot: 0));
        var seat1 = WorldMemberRef.Local(principal: WorldPrincipal.Seat(slot: 1));
        var console = WorldMemberRef.Local(principal: WorldPrincipal.Console);

        var section = new WorldGroupsSection(
            Kinds: [
                new WorldGroupKind(
                    Name: "friendship",
                    Roles: [new WorldGroupRole(Name: "friend", Capabilities: [WorldCapability.Observe])],
                    Lifetime: WorldGroupLifetime.Persistent,
                    EvictionPolicy: WorldGroupEvictionPolicy.Remove,
                    Capacity: 2
                ),
                new WorldGroupKind(
                    Name: "partnership",
                    Roles: [new WorldGroupRole(Name: "partner", Capabilities: [WorldCapability.Control])],
                    Lifetime: WorldGroupLifetime.Persistent,
                    EvictionPolicy: WorldGroupEvictionPolicy.Remove,
                    Capacity: 2
                ),
                new WorldGroupKind(
                    Name: "family",
                    Roles: [
                        new WorldGroupRole(Name: "guardian", Capabilities: [WorldCapability.Control]),
                        new WorldGroupRole(Name: "adult", Capabilities: [WorldCapability.Observe]),
                        new WorldGroupRole(Name: "child", Capabilities: [WorldCapability.Drive])
                    ],
                    Lifetime: WorldGroupLifetime.Persistent,
                    EvictionPolicy: WorldGroupEvictionPolicy.Remove,
                    Capacity: 3
                ),
                new WorldGroupKind(
                    Name: "guild",
                    Roles: [
                        new WorldGroupRole(Name: "leader", Capabilities: [WorldCapability.Control]),
                        new WorldGroupRole(Name: "officer", Capabilities: [WorldCapability.Mutate]),
                        new WorldGroupRole(Name: "member", Capabilities: [WorldCapability.Observe])
                    ],
                    Lifetime: WorldGroupLifetime.Persistent,
                    EvictionPolicy: WorldGroupEvictionPolicy.Remove,
                    Capacity: 3
                ),
                new WorldGroupKind(
                    Name: "party",
                    Roles: [
                        new WorldGroupRole(Name: "leader", Capabilities: [WorldCapability.Drive]),
                        new WorldGroupRole(Name: "member", Capabilities: [WorldCapability.Observe]),
                        new WorldGroupRole(Name: "activity", Capabilities: [WorldCapability.Control])
                    ],
                    Lifetime: WorldGroupLifetime.Ephemeral,
                    EvictionPolicy: WorldGroupEvictionPolicy.Remove,
                    Capacity: 3
                )
            ],
            Groups: [
                new WorldGroup(
                    Id: SafeName.Parse(candidate: "friendship-one"),
                    KindName: "friendship",
                    Members: [
                        new WorldGroupMember(Ref: qualified, Role: "friend", JoinOrdinal: 0),
                        new WorldGroupMember(Ref: seat0, Role: null, JoinOrdinal: 1)
                    ],
                    NextJoinOrdinal: 2
                ),
                new WorldGroup(
                    Id: SafeName.Parse(candidate: "friendship-two"),
                    KindName: "friendship",
                    Members: [
                        new WorldGroupMember(Ref: qualified, Role: "friend", JoinOrdinal: 0),
                        new WorldGroupMember(Ref: seat1, Role: "friend", JoinOrdinal: 1)
                    ],
                    NextJoinOrdinal: 2
                ),
                new WorldGroup(
                    Id: SafeName.Parse(candidate: "partnership-one"),
                    KindName: "partnership",
                    Members: [new WorldGroupMember(Ref: seat0, Role: "partner", JoinOrdinal: 0)],
                    NextJoinOrdinal: 1
                ),
                new WorldGroup(
                    Id: SafeName.Parse(candidate: "family-one"),
                    KindName: "family",
                    Members: [
                        new WorldGroupMember(Ref: seat0, Role: "guardian", JoinOrdinal: 0),
                        new WorldGroupMember(Ref: qualified, Role: "adult", JoinOrdinal: 1),
                        new WorldGroupMember(Ref: seat1, Role: "child", JoinOrdinal: 2)
                    ],
                    NextJoinOrdinal: 3
                ),
                new WorldGroup(
                    Id: SafeName.Parse(candidate: "guild-one"),
                    KindName: "guild",
                    Members: [
                        new WorldGroupMember(Ref: console, Role: "leader", JoinOrdinal: 0),
                        new WorldGroupMember(Ref: seat0, Role: "officer", JoinOrdinal: 1),
                        new WorldGroupMember(Ref: qualified, Role: "member", JoinOrdinal: 2)
                    ],
                    NextJoinOrdinal: 3
                ),
                new WorldGroup(
                    Id: SafeName.Parse(candidate: "party-one"),
                    KindName: "party",
                    Members: [
                        new WorldGroupMember(Ref: console, Role: "leader", JoinOrdinal: 0),
                        new WorldGroupMember(Ref: qualified, Role: "member", JoinOrdinal: 1),
                        new WorldGroupMember(Ref: seat1, Role: "activity", JoinOrdinal: 2)
                    ],
                    NextJoinOrdinal: 3
                )
            ],
            Ownership: []
        );

        var baseDefinition = WorldDefinitionSerialization.Deserialize(
            utf8Json: System.Text.Encoding.UTF8.GetBytes(s: """
                {
                  "schema": "puck.world.def.v1",
                  "documentId": "null"
                }
                """)
        );

        return baseDefinition with { Groups = section };
    }

    [Fact]
    public void Roster_DuplicateAndCapacityRefuse_AndDistinctMembersPass() {
        var first = new WorldGroupMember(
            Ref: WorldMemberRef.Local(principal: WorldPrincipal.Seat(slot: 0)),
            Role: "member",
            JoinOrdinal: 0,
            Tags: ["front"]
        );
        var duplicate = first with { JoinOrdinal = 1 };
        var second = new WorldGroupMember(
            Ref: WorldMemberRef.Local(principal: WorldPrincipal.Seat(slot: 1)),
            Role: "member",
            JoinOrdinal: 1,
            Tags: ["back"]
        );

        MembershipLawAssertions.RefusalWithControl(
            lawId: "world-group-roster.duplicate-reference",
            deniedOutcome: () => WorldGroupMembershipValidation.TryValidateRoster(
                members: [first, duplicate],
                capacity: 2,
                nextJoinOrdinal: 2,
                reason: out _
            ),
            controlOutcome: () => WorldGroupMembershipValidation.TryValidateRoster(
                members: [first, second],
                capacity: 2,
                nextJoinOrdinal: 2,
                reason: out _
            )
        );

        Assert.False(condition: WorldGroupMembershipValidation.TryValidateRoster(
            members: [first, second],
            capacity: 1,
            nextJoinOrdinal: 2,
            reason: out _
        ));
    }
}
