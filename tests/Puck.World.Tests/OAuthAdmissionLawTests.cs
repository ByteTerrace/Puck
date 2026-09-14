using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed class OAuthAdmissionLawTests {
    private const string Issuer = "https://issuer.example.test/tenant/v2.0";

    [InlineData(0)]
    [InlineData(3)]
    [Theory]
    public void DelegatedPeersKeepDistinctIdentitiesAndOnlyTheirOwnGrants(float graceSeconds) {
        WorldAdmissionEntry[] entries = [
            new(
                Issuer,
                "alice",
                WorldAdmissionTrustMode.OAuth,
                "",
                "",
                [new(
                        WorldCapability.Control,
                        GrantSubject.All
                    )],
                WorldDisclosureTier.Replica
            ),
            new(
                Algorithm: "",
                Disclosure: WorldDisclosureTier.Replica,
                Domain: Issuer,
                Grants: [],
                Mode: WorldAdmissionTrustMode.OAuth,
                PublicKey: "",
                Subject: "bob"
            ),
        ];
        var document = Fixtures.BuildDocument();
        using var fixture = Fixtures.FreshServer(document with { Admission = entries, PopulationRaw = document.Population with { CapacityRaw = 8, NetworkPlayers = 4, ReconnectGraceSeconds = graceSeconds } });
        var server = fixture.Server;

        Assert.True(condition: WorldAdmissionDoor.TryMatchOAuthEntry(
            entries: entries,
            issuer: Issuer,
            subject: "alice",
            verdict: out var alice
        ));
        Assert.True(condition: WorldAdmissionDoor.TryMatchOAuthEntry(
            entries: entries,
            issuer: Issuer,
            subject: "bob",
            verdict: out var bob
        ));
        Assert.True(
            condition: server.TryAdmitPeerConnection(
                alice,
                server.Definition.Admission,
                out var first,
                out var refusal
            ),
            userMessage: refusal
        );
        Assert.True(
            condition: server.TryAdmitPeerConnection(
                bob,
                server.Definition.Admission,
                out var second,
                out refusal
            ),
            userMessage: refusal
        );
        Assert.NotEqual(
            first.Identity,
            second.Identity
        );
        Assert.True(condition: server.Grants.Allows(
            first.Identity,
            WorldCapability.Control,
            GrantSubject.All
        ).IsAllowed);
        Assert.False(condition: server.Grants.Allows(
            second.Identity,
            WorldCapability.Control,
            GrantSubject.All
        ).IsAllowed);
        Assert.False(condition: server.Grants.Allows(
            first.Identity,
            WorldCapability.Edit,
            GrantSubject.All
        ).IsAllowed);
        server.DisconnectPeerConnection(peer: first);
        Assert.False(condition: server.Grants.Allows(
            first.Identity,
            WorldCapability.Control,
            GrantSubject.All
        ).IsAllowed);
        Assert.True(
            condition: server.TryAdmitPeerConnection(
                alice,
                server.Definition.Admission,
                out var replacement,
                out refusal
            ),
            userMessage: refusal
        );
        if (graceSeconds == 0) {
            Assert.NotEqual(
                first.Identity,
                replacement.Identity
            );
            Assert.False(condition: server.Grants.Allows(
                first.Identity,
                WorldCapability.Control,
                GrantSubject.All
            ).IsAllowed);
            server.DisconnectPeerConnection(peer: first);
        } else {
            // The existing lifecycle deliberately resumes a parked body for the same verified identity.
            Assert.Equal(
                first.Identity,
                replacement.Identity
            );
        }
        Assert.True(condition: server.Grants.Allows(
            replacement.Identity,
            WorldCapability.Control,
            GrantSubject.All
        ).IsAllowed);
        server.DisconnectPeerConnection(peer: second);
        server.DisconnectPeerConnection(peer: replacement);
    }
    [Fact]
    public void IssuerSubjectAndTrustModeAreAllRequired() {
        var entry = new WorldAdmissionEntry(
            Algorithm: "",
            Disclosure: WorldDisclosureTier.Replica,
            Domain: Issuer,
            Grants: [],
            Mode: WorldAdmissionTrustMode.OAuth,
            PublicKey: "",
            Subject: "alice"
        );

        Assert.True(condition: WorldAdmissionDoor.TryMatchOAuthEntry(
            entries: [entry],
            issuer: Issuer,
            subject: "alice",
            verdict: out var verdict
        ));
        Assert.Equal(
            Issuer,
            verdict.IdentityDomain
        );
        Assert.Equal(
            "alice",
            verdict.IdentitySubject
        );
        Assert.False(condition: WorldAdmissionDoor.TryMatchOAuthEntry(
            entries: [entry],
            issuer: (Issuer + "/"),
            subject: "alice",
            verdict: out _
        ));
        Assert.False(condition: WorldAdmissionDoor.TryMatchOAuthEntry(
            entries: [entry],
            issuer: Issuer,
            subject: "bob",
            verdict: out _
        ));
        Assert.False(condition: WorldAdmissionDoor.TryMatchOAuthEntry(
            entries: [entry with { Mode = WorldAdmissionTrustMode.SignsDirectly }],
            issuer: Issuer,
            subject: "alice",
            verdict: out _
        ));
        Assert.False(condition: WorldAdmissionDoor.TryMatchOAuthEntry(
            entries: [entry with { Subject = "*" }],
            issuer: Issuer,
            subject: "alice",
            verdict: out _
        ));
        Assert.False(condition: WorldAdmissionDoor.TryMatchOAuthEntry(
            entries: [],
            issuer: Issuer,
            subject: "alice",
            verdict: out _
        ));
    }
    [Fact]
    public void OAuthPolicyRejectsAmbiguousOrUnboundedIdentityConfiguration() {
        var row = new WorldAdmissionEntry(
            Algorithm: "",
            Disclosure: WorldDisclosureTier.Replica,
            Domain: Issuer,
            Grants: [],
            Mode: WorldAdmissionTrustMode.OAuth,
            PublicKey: "",
            Subject: "alice"
        );
        var document = Fixtures.BuildDocument() with { Admission = [row] };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: document,
                reason: out var reason
            ),
            userMessage: reason
        );
        WorldAdmissionEntry[] invalid = [row with { Domain = "http://issuer.example.test" }, row with { Domain = (Issuer + "?tenant=other") },
            row with { Domain = (Issuer + "#other") }, row with { Domain = (Issuer + new string(
                c: 'x',
                count: 4096
            )) },
            row with { Subject = "*" }, row with { Subject = "" }, row with { PublicKey = "key" }];

        foreach (var entry in invalid) { Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: document with { Admission = [entry] },
            reason: out _
        )); }
    }
}
