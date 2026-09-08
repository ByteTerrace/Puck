using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed class OAuthAdmissionLawTests {
    private const string Issuer = "https://issuer.example.test/tenant/v2.0";

    [Fact]
    public void OAuthPolicyRejectsAmbiguousOrUnboundedIdentityConfiguration() {
        var row = new WorldAdmissionEntry(Issuer, "alice", WorldAdmissionTrustMode.OAuth, "", "", [], WorldDisclosureTier.Replica);
        var document = Fixtures.BuildDocument() with { Admission = [row] };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(document, out var reason), reason);
        WorldAdmissionEntry[] invalid = [row with { Domain = "http://issuer.example.test" }, row with { Domain = Issuer + "?tenant=other" },
            row with { Domain = Issuer + "#other" }, row with { Domain = Issuer + new string('x', 4096) },
            row with { Subject = "*" }, row with { Subject = "" }, row with { PublicKey = "key" }];
        foreach (var entry in invalid) { Assert.False(WorldDefinitionValidator.TryValidateLocally(document with { Admission = [entry] }, out _)); }
    }

    [Fact]
    public void IssuerSubjectAndTrustModeAreAllRequired() {
        var entry = new WorldAdmissionEntry(Issuer, "alice", WorldAdmissionTrustMode.OAuth, "", "", [], WorldDisclosureTier.Replica);
        Assert.True(WorldAdmissionDoor.TryMatchOAuthEntry([entry], Issuer, "alice", out var verdict));
        Assert.Equal(Issuer, verdict.IdentityDomain);
        Assert.Equal("alice", verdict.IdentitySubject);
        Assert.False(WorldAdmissionDoor.TryMatchOAuthEntry([entry], Issuer + "/", "alice", out _));
        Assert.False(WorldAdmissionDoor.TryMatchOAuthEntry([entry], Issuer, "bob", out _));
        Assert.False(WorldAdmissionDoor.TryMatchOAuthEntry([entry with { Mode = WorldAdmissionTrustMode.SignsDirectly }], Issuer, "alice", out _));
        Assert.False(WorldAdmissionDoor.TryMatchOAuthEntry([entry with { Subject = "*" }], Issuer, "alice", out _));
        Assert.False(WorldAdmissionDoor.TryMatchOAuthEntry([], Issuer, "alice", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void DelegatedPeersKeepDistinctIdentitiesAndOnlyTheirOwnGrants(float graceSeconds) {
        WorldAdmissionEntry[] entries = [
            new(Issuer, "alice", WorldAdmissionTrustMode.OAuth, "", "", [new(WorldCapability.Control, GrantSubject.All)], WorldDisclosureTier.Replica),
            new(Issuer, "bob", WorldAdmissionTrustMode.OAuth, "", "", [], WorldDisclosureTier.Replica),
        ];
        var document = Fixtures.BuildDocument();
        using var fixture = Fixtures.FreshServer(document with { Admission = entries, PopulationRaw = document.Population with { CapacityRaw = 8, NetworkPlayers = 4, ReconnectGraceSeconds = graceSeconds } });
        var server = fixture.Server;
        Assert.True(WorldAdmissionDoor.TryMatchOAuthEntry(entries, Issuer, "alice", out var alice));
        Assert.True(WorldAdmissionDoor.TryMatchOAuthEntry(entries, Issuer, "bob", out var bob));
        Assert.True(server.TryAdmitPeerConnection(alice, server.Definition.Admission, out var first, out var refusal), refusal);
        Assert.True(server.TryAdmitPeerConnection(bob, server.Definition.Admission, out var second, out refusal), refusal);
        Assert.NotEqual(first.Identity, second.Identity);
        Assert.True(server.Grants.Allows(first.Identity, WorldCapability.Control, GrantSubject.All).IsAllowed);
        Assert.False(server.Grants.Allows(second.Identity, WorldCapability.Control, GrantSubject.All).IsAllowed);
        Assert.False(server.Grants.Allows(first.Identity, WorldCapability.Edit, GrantSubject.All).IsAllowed);
        server.DisconnectPeerConnection(first);
        Assert.False(server.Grants.Allows(first.Identity, WorldCapability.Control, GrantSubject.All).IsAllowed);
        Assert.True(server.TryAdmitPeerConnection(alice, server.Definition.Admission, out var replacement, out refusal), refusal);
        if (graceSeconds == 0) {
            Assert.NotEqual(first.Identity, replacement.Identity);
            Assert.False(server.Grants.Allows(first.Identity, WorldCapability.Control, GrantSubject.All).IsAllowed);
            server.DisconnectPeerConnection(first);
        } else {
            // The existing lifecycle deliberately resumes a parked body for the same verified identity.
            Assert.Equal(first.Identity, replacement.Identity);
        }
        Assert.True(server.Grants.Allows(replacement.Identity, WorldCapability.Control, GrantSubject.All).IsAllowed);
        server.DisconnectPeerConnection(second);
        server.DisconnectPeerConnection(replacement);
    }
}
