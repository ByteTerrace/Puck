using Puck.Commands;
using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Schema.Tests;

/// <summary>Round-1 laws for the local/verified membership value shape. Each refusal has a nearby passing control
/// that changes only the discriminating fact.</summary>
public sealed class WorldGroupMembershipLawTests {
    private static WorldDefinition SocialDefinition() {
        var identity = new WorldMemberIdentity(
            Issuer: "issuer.example",
            Subject: "subject-42",
            World: SafeName.Parse(candidate: "social-world")
        );
        var qualified = WorldMemberRef.VerifiedIdentity(identity: identity);
        var seat0 = WorldMemberRef.Local(principal: Principal.Seat(slot: 0));
        var seat1 = WorldMemberRef.Local(principal: Principal.Seat(slot: 1));
        var console = WorldMemberRef.Local(principal: Principal.Console);

        var section = new WorldGroupsSection(
            Kinds: [
                new WorldGroupKind(
                    Name: "friendship",
                    Roles: [new WorldGroupRole(
                            Capabilities: [WorldCapability.Observe],
                            Name: "friend"
                        )],
                    Lifetime: WorldGroupLifetime.Persistent,
                    EvictionPolicy: WorldGroupEvictionPolicy.Remove,
                    Capacity: 2
                ),
                new WorldGroupKind(
                    Name: "partnership",
                    Roles: [new WorldGroupRole(
                            Capabilities: [WorldCapability.Control],
                            Name: "partner"
                        )],
                    Lifetime: WorldGroupLifetime.Persistent,
                    EvictionPolicy: WorldGroupEvictionPolicy.Remove,
                    Capacity: 2
                ),
                new WorldGroupKind(
                    Name: "family",
                    Roles: [
                        new WorldGroupRole(
                            Capabilities: [WorldCapability.Control],
                            Name: "guardian"
                        ),
                        new WorldGroupRole(
                            Capabilities: [WorldCapability.Observe],
                            Name: "adult"
                        ),
                        new WorldGroupRole(
                            Capabilities: [WorldCapability.Drive],
                            Name: "child"
                        )
                    ],
                    Lifetime: WorldGroupLifetime.Persistent,
                    EvictionPolicy: WorldGroupEvictionPolicy.Remove,
                    Capacity: 3
                ),
                new WorldGroupKind(
                    Name: "guild",
                    Roles: [
                        new WorldGroupRole(
                            Capabilities: [WorldCapability.Control],
                            Name: "leader"
                        ),
                        new WorldGroupRole(
                            Capabilities: [WorldCapability.Mutate],
                            Name: "officer"
                        ),
                        new WorldGroupRole(
                            Capabilities: [WorldCapability.Observe],
                            Name: "member"
                        )
                    ],
                    Lifetime: WorldGroupLifetime.Persistent,
                    EvictionPolicy: WorldGroupEvictionPolicy.Remove,
                    Capacity: 3
                ),
                new WorldGroupKind(
                    Name: "party",
                    Roles: [
                        new WorldGroupRole(
                            Capabilities: [WorldCapability.Drive],
                            Name: "leader"
                        ),
                        new WorldGroupRole(
                            Capabilities: [WorldCapability.Observe],
                            Name: "member"
                        ),
                        new WorldGroupRole(
                            Capabilities: [WorldCapability.Control],
                            Name: "activity"
                        )
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
                        new WorldGroupMember(
                            Ref: qualified,
                            Role: "friend",
                            JoinOrdinal: 0
                        ),
                        new WorldGroupMember(
                            Ref: seat0,
                            Role: null,
                            JoinOrdinal: 1
                        )
                    ],
                    NextJoinOrdinal: 2
                ),
                new WorldGroup(
                    Id: SafeName.Parse(candidate: "friendship-two"),
                    KindName: "friendship",
                    Members: [
                        new WorldGroupMember(
                            Ref: qualified,
                            Role: "friend",
                            JoinOrdinal: 0
                        ),
                        new WorldGroupMember(
                            Ref: seat1,
                            Role: "friend",
                            JoinOrdinal: 1
                        )
                    ],
                    NextJoinOrdinal: 2
                ),
                new WorldGroup(
                    Id: SafeName.Parse(candidate: "partnership-one"),
                    KindName: "partnership",
                    Members: [new WorldGroupMember(
                            Ref: seat0,
                            Role: "partner",
                            JoinOrdinal: 0
                        )],
                    NextJoinOrdinal: 1
                ),
                new WorldGroup(
                    Id: SafeName.Parse(candidate: "family-one"),
                    KindName: "family",
                    Members: [
                        new WorldGroupMember(
                            Ref: seat0,
                            Role: "guardian",
                            JoinOrdinal: 0
                        ),
                        new WorldGroupMember(
                            Ref: qualified,
                            Role: "adult",
                            JoinOrdinal: 1
                        ),
                        new WorldGroupMember(
                            Ref: seat1,
                            Role: "child",
                            JoinOrdinal: 2
                        )
                    ],
                    NextJoinOrdinal: 3
                ),
                new WorldGroup(
                    Id: SafeName.Parse(candidate: "guild-one"),
                    KindName: "guild",
                    Members: [
                        new WorldGroupMember(
                            Ref: console,
                            Role: "leader",
                            JoinOrdinal: 0
                        ),
                        new WorldGroupMember(
                            Ref: seat0,
                            Role: "officer",
                            JoinOrdinal: 1
                        ),
                        new WorldGroupMember(
                            Ref: qualified,
                            Role: "member",
                            JoinOrdinal: 2
                        )
                    ],
                    NextJoinOrdinal: 3
                ),
                new WorldGroup(
                    Id: SafeName.Parse(candidate: "party-one"),
                    KindName: "party",
                    Members: [
                        new WorldGroupMember(
                            Ref: console,
                            Role: "leader",
                            JoinOrdinal: 0
                        ),
                        new WorldGroupMember(
                            Ref: qualified,
                            Role: "member",
                            JoinOrdinal: 1
                        ),
                        new WorldGroupMember(
                            Ref: seat1,
                            Role: "activity",
                            JoinOrdinal: 2
                        )
                    ],
                    NextJoinOrdinal: 3
                )
            ],
            Ownership: []
        );

        var baseDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: """
                {
                  "schema": "puck.world.definition.v1",
                  "documentId": "null"
                }
                """));

        return baseDefinition with { Groups = section };
    }

    [Fact]
    public void Identity_MalformedLengthWorldAndControlInputRefuse_AndCanonicalControlPasses() {
        var identity = new WorldMemberIdentity(
            Issuer: "issuer",
            Subject: "subject",
            World: SafeName.Parse(candidate: "arena")
        );
        var canonical = identity.Describe();

        Assert.False(condition: WorldMemberIdentity.TryParseCanonical(
            identity: out _,
            token: "member:01~a7~subject"
        ));
        Assert.False(condition: WorldMemberIdentity.TryParseCanonical(
            identity: out _,
            token: "member:999999999999999999999~a1~b"
        ));
        Assert.False(condition: WorldMemberIdentity.TryParseCanonical(
            identity: out _,
            token: "member:2~a1~b"
        ));
        Assert.False(condition: WorldMemberIdentity.TryParseCanonical(
            identity: out _,
            token: $"{canonical}1~x"
        ));
        Assert.False(condition: new WorldMemberIdentity(
            Issuer: "issuer\0",
            Subject: "subject",
            World: null
        ).IsCanonical());
        Assert.False(condition: new WorldMemberIdentity(
            Issuer: "issuer",
            Subject: "subject",
            World: default(SafeName)
        ).IsCanonical());
        Assert.True(condition: WorldMemberIdentity.TryParseCanonical(
            identity: out _,
            token: canonical
        ));
    }
    [Fact]
    public void Identity_NamesThatDifferOnlyAtASeparatorRemainInjective() {
        var first = new WorldMemberIdentity(
            Issuer: "ab",
            Subject: "c",
            World: null
        );
        var second = new WorldMemberIdentity(
            Issuer: "a",
            Subject: "bc",
            World: null
        );

        Assert.NotEqual(
            expected: first.Describe(),
            actual: second.Describe()
        );
        Assert.True(condition: WorldMemberIdentity.TryParseCanonical(
            token: first.Describe(),
            identity: out _
        ));
        Assert.True(condition: WorldMemberIdentity.TryParseCanonical(
            token: second.Describe(),
            identity: out _
        ));
    }
    [Fact]
    public void Identity_NonCanonicalSpellingRefuses_AndCanonicalControlParses() {
        var identity = new WorldMemberIdentity(
            Issuer: "issuer",
            Subject: "subject",
            World: null
        );
        var nonCanonical = identity.Describe().Replace(
            comparisonType: StringComparison.Ordinal,
            newValue: "MEMBER:",
            oldValue: "member:"
        );

        MembershipLawAssertions.RefusalWithControl(
            lawId: "world-member-identity.canonical-spelling",
            deniedOutcome: () => WorldMemberIdentity.TryParseCanonical(
                identity: out _,
                token: nonCanonical
            ),
            controlOutcome: () => WorldMemberIdentity.TryParseCanonical(
                token: identity.Describe(),
                identity: out _
            )
        );
    }
    [Fact]
    public void Identity_UsesLengthPrefixedSegments_AndRoundTripsSeparators() {
        var identity = new WorldMemberIdentity(
            Issuer: "https://issuer.example/a:b~c",
            Subject: "user:1~2",
            World: SafeName.Parse(candidate: "arena")
        );

        var token = identity.Describe();

        Assert.True(condition: WorldMemberIdentity.TryParseCanonical(
            identity: out var parsed,
            token: token
        ));
        Assert.Equal(
            actual: parsed,
            expected: identity
        );
        Assert.True(condition: identity.IsCanonical());
    }
    [Fact]
    public void MemberRef_DefaultAndMismatchedPayloadsRefuse_AndCanonicalArmsPass() {
        var identity = new WorldMemberIdentity(
            Issuer: "issuer",
            Subject: "subject",
            World: null
        );
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
        Assert.False(condition: new WorldMemberRef(Kind: ((MemberRefKind)99)).IsCanonical());
    }
    [Fact]
    public void MemberRef_LocalCannotSatisfyVerifiedArm() {
        var local = WorldMemberRef.Local(principal: Principal.Seat(slot: 0));

        Assert.True(condition: local.IsCanonical());
        Assert.False(condition: WorldMemberRef.TryParseCanonical(
            token: $"verified:{local.Describe()}",
            member: out _
        ));
        Assert.False(condition: WorldMemberRef.TryParseCanonical(
            member: out _,
            token: "local:verified:member:6~issuer7~subject"
        ));
    }
    [Fact]
    public void MembershipJson_RejectsUnknownNestedMembers() {
        var validJson = "{\"ref\":{\"kind\":\"Verified\",\"verified\":{\"issuer\":\"issuer\",\"subject\":\"subject\",\"world\":null}},\"role\":null,\"joinOrdinal\":0}";
        var json = validJson.Replace(
            comparisonType: StringComparison.Ordinal,
            newValue: "\"world\":null,\"extra\":\"x\"}",
            oldValue: "\"world\":null}"
        );

        Assert.NotNull(@object: System.Text.Json.JsonSerializer.Deserialize<WorldGroupMember>(
            json: validJson,
            options: WorldJsonContext.Default.Options
        ));
        Assert.Throws<System.Text.Json.JsonException>(testCode: () => System.Text.Json.JsonSerializer.Deserialize<WorldGroupMember>(
            json: json,
            options: WorldJsonContext.Default.Options
        ));
    }
    [Fact]
    public void Roster_DuplicateAndCapacityRefuse_AndDistinctMembersPass() {
        var first = new WorldGroupMember(
            Ref: WorldMemberRef.Local(principal: Principal.Seat(slot: 0)),
            Role: "member",
            JoinOrdinal: 0,
            Tags: ["front"]
        );
        var duplicate = first with { JoinOrdinal = 1 };
        var second = new WorldGroupMember(
            Ref: WorldMemberRef.Local(principal: Principal.Seat(slot: 1)),
            Role: "member",
            JoinOrdinal: 1,
            Tags: ["back"]
        );

        MembershipLawAssertions.RefusalWithControl(
            lawId: "world-group-roster.duplicate-reference",
            deniedOutcome: () => WorldGroupMembershipValidation.TryValidateRoster(
                capacity: 2,
                members: [first, duplicate],
                nextJoinOrdinal: 2,
                reason: out _
            ),
            controlOutcome: () => WorldGroupMembershipValidation.TryValidateRoster(
                capacity: 2,
                members: [first, second],
                nextJoinOrdinal: 2,
                reason: out _
            )
        );

        Assert.False(condition: WorldGroupMembershipValidation.TryValidateRoster(
            capacity: 1,
            members: [first, second],
            nextJoinOrdinal: 2,
            reason: out _
        ));
    }
    [Fact]
    public void SocialKinds_FullDefinitionValidationAcceptsDistinctKindsAndRosters() {
        // These names and role labels are test vocabulary only: the engine ships no social-kind catalog or finder.
        // This law proves that one complete authored groups section can carry the intended relationship shapes.
        var definition = SocialDefinition();

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var reason
            ),
            userMessage: reason
        );

        var groups = definition.Groups!;

        Assert.Equal(
            expected: 5,
            actual: groups.Kinds.Count
        );
        Assert.Equal(
            expected: 6,
            actual: groups.Groups.Count
        );
        Assert.Equal(
            expected: groups.Groups[0].Members[0].Ref,
            actual: groups.Groups[1].Members[0].Ref
        );
        Assert.Equal(
            expected: "friend",
            actual: groups.Groups[0].Members[0].Role
        );
        Assert.Equal(
            expected: "friend",
            actual: groups.Groups[1].Members[0].Role
        );
        Assert.NotEqual(
            expected: groups.Groups[0].Members[0].Ref,
            actual: groups.Groups[2].Members[0].Ref
        );
        Assert.Null(@object: groups.Groups[0].Members[1].Role);
    }
    [Fact]
    public void SocialKinds_FullDefinitionValidationRefusesUndeclaredRoleAndOverCapacity() {
        var definition = SocialDefinition();
        var groups = definition.Groups!;

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var validReason
            ),
            userMessage: validReason
        );

        var wrongRole = groups.Groups[0] with {
            Members = [groups.Groups[0].Members[0] with { Role = "not-declared" }, groups.Groups[0].Members[1]],
        };
        var wrongRoleGroups = groups.Groups.ToArray();

        wrongRoleGroups[0] = wrongRole;
        var wrongRoleDefinition = definition with { Groups = groups with { Groups = wrongRoleGroups } };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: wrongRoleDefinition,
            reason: out var roleReason
        ));
        Assert.Contains(
            actualString: roleReason,
            expectedSubstring: "names no role declared"
        );

        var friendship = groups.Groups[0];
        var overCapacity = friendship with {
            Members = [
                ..friendship.Members,
                new WorldGroupMember(
                Ref: WorldMemberRef.Local(principal: Principal.Seat(slot: 2)),
                Role: "friend",
                JoinOrdinal: 2
            )
            ],
            NextJoinOrdinal = 3,
        };
        var overCapacityGroups = groups.Groups.ToArray();

        overCapacityGroups[0] = overCapacity;
        var overCapacityDefinition = definition with { Groups = groups with { Groups = overCapacityGroups } };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: overCapacityDefinition,
            reason: out var capacityReason
        ));
        Assert.Contains(
            actualString: capacityReason,
            expectedSubstring: "exceeding capacity"
        );
    }
}
