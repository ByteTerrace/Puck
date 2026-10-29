using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>The <c>networkPlayers</c> admission cap counts remote humans only (the owner's ruling): an autonomous
/// traveler such as a companion and a census bot never spend it. The destination decides who is a human from what it
/// hands out, never from a sender's claim: a traveler keeping the Live source keeps an outside driver and lands as a
/// human whatever the sender says, and a sender's statement that a human holds a body can only add a human.</summary>
public sealed class NetworkPlayersCapLawTests {
    private const string SourceAuthority = "machine-a/plaza";

    private static WorldPopulation Population(int networkPlayers, int peerSlots) =>
        new(definition: Document(networkPlayers: networkPlayers, peerSlots: peerSlots));
    private static WorldDefinition Document(int networkPlayers, int peerSlots) {
        var document = Fixtures.PeerPopulationDocument(networkPlayers: networkPlayers);

        return document with {
            PopulationRaw = document.Population with {
                CapacityRaw = (document.Population.LocalSeats + peerSlots),
            },
        };
    }
    private static bool AdmitTraveler(WorldPopulation population, int slot, out string refusal) => population.TryAdmitTransferredEntityAt(
        admitted: out _,
        occupant: new WorldTransferredOccupant(
            CatalogRig: 0,
            TravelTurn: FixedQ4816.Zero
        ),
        refusal: out refusal,
        slot: slot,
        source: IntentSource.Idle
    );
    private static bool AdmitHuman(WorldPopulation population, int slot, out string refusal) => population.TryAdmitRemotePeerAt(
        admitted: out _,
        grantTemplates: [],
        identityDomain: "domain",
        identitySubject: $"human-{slot}",
        refusal: out refusal,
        slot: slot,
        source: IntentSource.Live
    );
    // A traveler as a federation authority states it: decoded from the wire, so the destination sees exactly what a
    // hostile sender could put there.
    private static WorldTransferReservationRequest Federated(ulong transferId, IntentSource source, bool remoteHuman) {
        var incarnation = new WorldEntityAddress(
            Authority: "origin/world",
            Generation: 7,
            Index: checked(((int)transferId) + WorldBodiesLimits.LocalSeatCount)
        );
        var request = new WorldTransferReservationRequest(
            TransferId: transferId,
            SourceAuthority: SourceAuthority,
            SourceRateHz: 240,
            SourceTick: 0,
            DeadlineSourceTick: 60,
            Border: "east",
            BorderCapacity: null,
            PartyAllOrNothing: true,
            PeerAdmission: true,
            Members: [new WorldTransferReservationMember(
                Principal: Principal.Console,
                PreferredSlot: WorldBodiesLimits.LocalSeatCount,
                Identity: null,
                Source: source,
                BodyColor: default,
                CatalogRig: 0,
                Mobility: new WorldMobilityIdentity(
                    DepartedFrom: incarnation,
                    Epoch: 0,
                    Incarnation: incarnation
                ),
                RemoteHuman: remoteHuman
            )]
        );

        Assert.True(condition: WorldFederationCodec.TryDecodeReservation(
            body: WorldFederationCodec.EncodeReservation(request: request),
            request: out var decoded,
            failure: out var failure
        ), userMessage: failure.ToString());

        return decoded!;
    }
    // Reserves and commits one federated traveler; returns the body it landed on, or the refusal that stopped it.
    private static (int? Slot, string Refusal) Land(WorldFixture fixture, ulong transferId, IntentSource source, bool remoteHuman) {
        var reservation = fixture.Server.ReserveTransfer(request: Federated(
            remoteHuman: remoteHuman,
            source: source,
            transferId: transferId
        ));

        if (!reservation.Accepted) {
            return (null, reservation.Reason);
        }

        var status = fixture.Server.CommitTransfer(
            sourceAuthority: SourceAuthority,
            transferId: transferId,
            members: [new WorldTransferCommitMember(
                Profile: null,
                Arrival: WorldTransferArrival.Spawn,
                BodyMotionProgramName: "grounded",
                Position: default,
                YawRadians: default,
                PlanarVelocity: default,
                VerticalVelocity: default
            )],
            reason: out var reason
        );

        return ((status == WorldTransferStatus.Committed)
            ? (reservation.BodyIndices.Single(), string.Empty)
            : (null, reason));
    }

    [Fact]
    public void TravelersAndCensusBotsNeverSpendTheCapButHumansDo() {
        var population = Population(networkPlayers: 1, peerSlots: 5);
        var first = population.LocalSeatCount;

        Assert.True(condition: AdmitTraveler(population, first, out var refusal), userMessage: refusal);
        Assert.True(condition: AdmitTraveler(population, (first + 1), out refusal), userMessage: refusal);
        Assert.Equal(expected: 1, actual: population.SetSimulatedCount(count: 1));
        Assert.True(condition: AdmitHuman(population, (first + 4), out refusal), userMessage: refusal);
        Assert.False(condition: AdmitHuman(population, (first + 3), out refusal));
        Assert.Contains(expectedSubstring: "networkPlayers admission cap (1) is already met", actualString: refusal);
    }
    /// <summary>A federation sender that states a Live traveler is no human cannot land it into a zero cap: the Live
    /// source keeps an outside driver, so it would be a remote human holding the arrival's Drive grants. The control is
    /// the same claim at a cap of one, where it lands counted as a human.</summary>
    [Fact]
    public void AFederatedLiveTravelerClaimingNoHumanIsRefusedAtAZeroCap() {
        using (var refused = Fixtures.FreshServer(definition: Document(networkPlayers: 0, peerSlots: 2))) {
            var (slot, refusal) = Land(fixture: refused, remoteHuman: false, source: IntentSource.Live, transferId: 1);

            Assert.Null(@object: slot);
            Assert.Contains(expectedSubstring: "networkPlayers admission cap (0) is already met", actualString: refusal);
            Assert.False(condition: refused.Server.Population.IsAdmittedPeer(bodyIndex: WorldBodiesLimits.LocalSeatCount));
        }

        using var control = Fixtures.FreshServer(definition: Document(networkPlayers: 1, peerSlots: 2));
        var (landed, reason) = Land(fixture: control, remoteHuman: false, source: IntentSource.Live, transferId: 1);

        Assert.True(condition: landed.HasValue, userMessage: reason);
        Assert.True(condition: control.Server.Population.IsAdmittedPeer(bodyIndex: landed!.Value));
    }
    /// <summary>A human whose control source was set to Idle before it left still lands as a human: it spends the cap
    /// (refused at zero, and the second of two refused at one), keeps its Idle source, and stays a remote human rather
    /// than becoming an autonomous entity.</summary>
    [Fact]
    public void AnIdleHumanStillCountsAndStaysHuman() {
        using (var refused = Fixtures.FreshServer(definition: Document(networkPlayers: 0, peerSlots: 2))) {
            var (slot, refusal) = Land(fixture: refused, remoteHuman: true, source: IntentSource.Idle, transferId: 1);

            Assert.Null(@object: slot);
            Assert.Contains(expectedSubstring: "networkPlayers admission cap (0) is already met", actualString: refusal);
        }

        using var fixture = Fixtures.FreshServer(definition: Document(networkPlayers: 1, peerSlots: 2));
        var (landed, reason) = Land(fixture: fixture, remoteHuman: true, source: IntentSource.Idle, transferId: 1);

        Assert.True(condition: landed.HasValue, userMessage: reason);
        Assert.True(condition: fixture.Server.Population.IsAdmittedPeer(bodyIndex: landed!.Value));
        Assert.True(condition: fixture.Server.Population.EntryBody(index: landed.Value)!.Source.IsIdle);

        var (second, secondRefusal) = Land(fixture: fixture, remoteHuman: true, source: IntentSource.Idle, transferId: 2);

        Assert.Null(@object: second);
        Assert.Contains(expectedSubstring: "networkPlayers admission cap (1) is already met", actualString: secondRefusal);
    }
    /// <summary>An autonomous traveler, as a companion crosses, lands into a zero cap and spends nothing: the
    /// destination authors it, with no Drive grant, and nothing its sender forwards reaches it.</summary>
    [Fact]
    public void AnAutonomousTravelerLandsAtAZeroCapWithoutSpendingIt() {
        using var fixture = Fixtures.FreshServer(definition: Document(networkPlayers: 0, peerSlots: 2));
        var (landed, reason) = Land(fixture: fixture, remoteHuman: false, source: IntentSource.Idle, transferId: 1);

        Assert.True(condition: landed.HasValue, userMessage: reason);

        var server = fixture.Server;
        var principal = server.Population.PeerPrincipal(index: landed!.Value);

        Assert.False(condition: server.Population.IsAdmittedPeer(bodyIndex: landed.Value));
        Assert.DoesNotContain(collection: server.GrantRows(principal: principal), filter: static row => (row.Capability == WorldCapability.Drive));
        Assert.False(condition: server.ExecuteAuthorityOperation(operation: () => WorldLocalForwardedAuthority.IsLiveTransferredPrincipal(
            principal: principal,
            server: server
        )));
    }
}
