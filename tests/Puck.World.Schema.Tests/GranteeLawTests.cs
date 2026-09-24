using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="Grantee"/>, what a grant row names as its holder. Every principal is a
/// grantee and keeps its own label; a group and a document are grantees that are never principals, so the principal
/// grammar refuses their tokens while the grantee grammar reads them; and every label round-trips canonically.</summary>
public sealed class GranteeLawTests {
    public static TheoryData<string> Labels() => [
        "seat1",
        "seat4",
        "console",
        "world",
        "addon:guide",
        "peer:5:2",
        "group:readers",
        "document:mail",
    ];
    [MemberData(memberName: nameof(Labels))]
    [Theory]
    public void EveryGranteeLabelRoundTripsCanonically(string label) {
        Assert.True(condition: Grantee.TryParseCanonical(
            grantee: out var grantee,
            token: label
        ));
        Assert.Equal(
            actual: grantee.Describe(),
            expected: label
        );
        Assert.True(condition: grantee.IsCanonical());
    }
    [InlineData("group:readers")]
    [InlineData("document:mail")]
    [Theory]
    public void AGroupOrADocumentIsNeverAPrincipal(string label) {
        Assert.False(condition: PrincipalTokens.TryParse(
            principal: out _,
            token: label
        ));
        Assert.True(condition: Grantee.TryParse(
            grantee: out var grantee,
            token: label
        ));
        Assert.False(condition: grantee.TryGetPrincipal(principal: out _));
    }
    [Fact]
    public void APrincipalConvertsToTheGranteeItsLabelParsesTo() {
        var peer = Principal.Peer(
            generation: 3,
            index: 7
        );
        Grantee converted = peer;

        Assert.True(condition: Grantee.TryParse(
            grantee: out var parsed,
            token: peer.Describe()
        ));
        Assert.Equal(
            actual: converted,
            expected: parsed
        );
        Assert.True(condition: converted.TryGetPrincipal(principal: out var back));
        Assert.Equal(
            actual: back,
            expected: peer
        );
    }
    [Fact]
    public void AnUnstampedPrincipalIsNoIdentity() {
        Assert.False(condition: default(Principal).IsStamped);
        Assert.False(condition: default(Principal).IsCanonical());
        Assert.True(condition: Principal.Console.IsCanonical());
    }
}
