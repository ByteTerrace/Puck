using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: one connection-loss event has ONE timing rule. A disconnect that PARKS a peer body keeps that
/// generation's grant rows through the grace window — a reconnect inside it resumes onto live authority instead of a
/// re-mint — and the rows are revoked at the same <c>ParkedUntilTick</c> deadline that reclaims the body. A
/// disconnect that does NOT park (an authored-zero grace) still releases immediately, unchanged.
/// <para>The discriminating pair is the authored <c>population.reconnectGraceSeconds</c>: positive parks, zero does
/// not, and the SAME disconnect over the SAME body is measured both ways against the grant table.</para>
/// </summary>
public sealed class ParkedGrantReleaseLawTests {
    private const int PeerBodyIndex = 4;
    private const int PeerGeneration = 1;

    private static WorldDefinition WithGrace(float seconds) {
        var definition = Fixtures.BuildDocument();

        return (definition with {
            PopulationRaw = (definition.Population with {
                CapacityRaw = (definition.Population.LocalSeats + 1),
                NetworkPlayers = 1,
                ReconnectGraceSeconds = seconds,
            }),
        });
    }
    private static WorldPeerEventEntry PeerEntry(WorldPrincipal peer) => new(
        BodyIndex: PeerBodyIndex,
        Generation: peer.Generation,
        Source: IntentSource.Live,
        Identity: peer,
        IdentityDomain: "test",
        IdentitySubject: "peer",
        AuthorityTransferred: false,
        PlacementId: null,
        CatalogRig: 0
    );
    private static WorldPrincipal AdmitPeer(WorldFixture fixture) {
        var peer = WorldPrincipal.Peer(
            index: PeerBodyIndex,
            generation: PeerGeneration
        );

        fixture.Server.ApplyServerEvent(serverEvent: new WorldServerEvent.PeerAdmitted(
            Entries: [
                PeerEntry(peer: peer),
            ],
            MintedGrants: []
        ));

        // The population owns the generation the admitted entry actually took, so the identity every later check
        // reads is resolved from it rather than assumed.
        peer = fixture.Server.Population.PeerPrincipal(index: PeerBodyIndex);

        // Minted through the ordinary administration door rather than the event's own MintedGrants lane, so the row
        // under test is unambiguously installed before the disconnect is measured against it.
        fixture.Server.Grant(
            grant: new WorldGrant(
                Principal: peer,
                Capability: WorldCapability.Drive,
                Subject: GrantSubject.Body(index: PeerBodyIndex),
                Exclusive: false,
                // An untrusted principal's Drive row must name its own per-tick dispatch allowance explicitly.
                Budget: 64
            ),
            actor: WorldPrincipal.Console
        );

        return peer;
    }
    private static void Disconnect(WorldFixture fixture, WorldPrincipal peer) =>
        fixture.Server.ApplyServerEvent(serverEvent: new WorldServerEvent.PeerDisconnected(
            Entries: [
                PeerEntry(peer: peer),
            ],
            RevokedGrants: [.. fixture.Server.Grants.Held(principal: peer).Select(selector: row => new WorldGrant(
                Principal: peer,
                Capability: row.Capability,
                Subject: row.Subject,
                Exclusive: false
            ))]
        ));
    private static bool Drives(WorldFixture fixture, WorldPrincipal peer) =>
        fixture.Server.Grants.Allows(
            principal: peer,
            capability: WorldCapability.Drive,
            subject: GrantSubject.Body(index: PeerBodyIndex)
        ).IsAllowed;

    [Fact]
    public void ParkedDisconnectRetainsGrantsThroughTheGraceWindow() {
        using var fixture = Fixtures.FreshServer(definition: WithGrace(seconds: 3.0f));

        var peer = AdmitPeer(fixture: fixture);

        Assert.True(condition: Drives(
            fixture: fixture,
            peer: peer
        ));

        Disconnect(
            fixture: fixture,
            peer: peer
        );

        // The body parked, so the rows ride the same deadline — one step is nowhere near a 3-second grace.
        fixture.Step();

        Assert.True(condition: Drives(
            fixture: fixture,
            peer: peer
        ));
    }
    [Fact]
    public void DisconnectWithGraceDisabledReleasesGrantsImmediately() {
        using var fixture = Fixtures.FreshServer(definition: WithGrace(seconds: 0f));

        var peer = AdmitPeer(fixture: fixture);

        Assert.True(condition: Drives(
            fixture: fixture,
            peer: peer
        ));

        // The one discriminating fact against the law above: an authored-zero grace parks nothing, so the teardown
        // and the grant release are both immediate — the pre-park behavior, unchanged.
        Disconnect(
            fixture: fixture,
            peer: peer
        );

        Assert.False(condition: Drives(
            fixture: fixture,
            peer: peer
        ));
    }
    [Fact]
    public void ParkedGrantsAreReleasedWhenTheReclaimSweepFires() {
        // A one-tick grace at 240 Hz — long enough to park, short enough that a handful of steps crosses it.
        using var fixture = Fixtures.FreshServer(definition: WithGrace(seconds: (1f / 240f)));

        var peer = AdmitPeer(fixture: fixture);

        Disconnect(
            fixture: fixture,
            peer: peer
        );

        for (var step = 0; (step < 8); step++) {
            fixture.Step();
        }

        Assert.False(condition: Drives(
            fixture: fixture,
            peer: peer
        ));
    }
}
