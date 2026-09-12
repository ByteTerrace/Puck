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
