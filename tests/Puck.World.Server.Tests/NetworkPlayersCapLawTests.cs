using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>The <c>networkPlayers</c> admission cap counts remote humans only (the owner's ruling): an autonomous
/// traveler such as a companion and a census bot never spend it.</summary>
public sealed class NetworkPlayersCapLawTests {
    private static WorldPopulation Population(int networkPlayers, int peerSlots) {
        var document = Fixtures.PeerPopulationDocument(networkPlayers: networkPlayers);

        return new WorldPopulation(definition: (document with {
            PopulationRaw = document.Population with {
                CapacityRaw = (document.Population.LocalSeats + peerSlots),
            },
        }));
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
    [Fact]
    public void ACapOfZeroStillAdmitsTravelersAndRefusesHumans() {
        var population = Population(networkPlayers: 0, peerSlots: 3);
        var first = population.LocalSeatCount;

        Assert.True(condition: AdmitTraveler(population, first, out var refusal), userMessage: refusal);
        // A bot that reads a live source and arrives through the peer door, as a companion does.
        Assert.True(condition: population.TryAdmitRemotePeerAt(
            admitted: out var companion,
            authorityTransferred: true,
            grantTemplates: [],
            identityDomain: "domain",
            identitySubject: "companion",
            refusal: out refusal,
            remoteHuman: false,
            slot: (first + 1),
            source: IntentSource.Live
        ), userMessage: refusal);
        Assert.False(condition: companion.RemoteHuman);
        Assert.False(condition: AdmitHuman(population, (first + 2), out refusal));
        Assert.Contains(expectedSubstring: "networkPlayers admission cap (0) is already met", actualString: refusal);
    }
}
