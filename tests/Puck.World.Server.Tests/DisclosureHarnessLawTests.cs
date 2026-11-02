using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>The noninterference harness's own laws, over in-process pairs of worlds: two worlds whose hidden state is
/// equal compare equal at every door a replica and a presentation session reach, and a world that differs in what a
/// session may read is told apart at the door that delivered it. The harness's verdicts on real fixtures come from
/// <c>puck disclosure</c>, which boots them through the real executable.</summary>
public sealed class DisclosureHarnessLawTests {
    private static readonly string[] Words = ["hand", "pub", "secret", "nothing", "0", "1", "7"];

    private static WorldDefinition Document(long pub, long secret) => Fixtures.BuildDocument() with {
        Admission = [
            new WorldAdmissionEntry(Disclosure: WorldDisclosureTier.Replica, Domain: "replica", Mode: WorldAdmissionTrustMode.FederatedAuthority,
                Grants: [new WorldAdmissionGrant(Budget: 64, Capability: WorldCapability.Observe, Subject: GrantSubject.All)]),
            new WorldAdmissionEntry(Disclosure: WorldDisclosureTier.Presentation, Domain: "presentation", Mode: WorldAdmissionTrustMode.FederatedAuthority,
                Grants: [new WorldAdmissionGrant(Budget: 64, Capability: WorldCapability.Observe, Subject: GrantSubject.All)]),
        ],
        StateRaw = new WorldStateSection(World: [new WorldStateRow(
            Name: CellName.Parse(candidate: "hand"), Kind: CellKind.Int,
            Visibility: new StateVisibility(Hidden: HiddenCells.Omit),
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "pub"), Value: CellValue.Int(value: pub)),
                new StateCell(Key: CellName.Parse(candidate: "secret"), Value: CellValue.Int(value: secret),
                    Visibility: new StateVisibility(Readers: ["seat1"])),
            ])]),
    };

    // One world of a pair: a replica and a presentation session observe it while each performs the same drawn actions.
    private static WorldDisclosureTranscript Run(WorldDefinition definition) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var transcript = new WorldDisclosureTranscript();
        WorldDisclosureSession[] sessions = [
            WorldDisclosureSession.Observe(seed: 5UL, server: fixture.Server, sourceAuthority: "replica", transcript: transcript),
            WorldDisclosureSession.Observe(seed: 5UL, server: fixture.Server, sourceAuthority: "presentation", transcript: transcript),
        ];
        var actions = sessions.Select(selector: (_, index) => WorldDisclosureFuzzer.Draw(count: 12, from: 1UL, seed: (5UL + (ulong)index), until: 10UL,
            verbs: WorldDisclosureSession.Actions, words: Words)).ToArray();

        for (var tick = 1UL; (tick <= 12UL); tick++) {
            fixture.Step();
            for (var index = 0; (index < sessions.Length); index++) {
                foreach (var input in actions[index].Where(predicate: input => (input.Tick == tick))) { sessions[index].Act(action: input.Text); }
            }
        }
        foreach (var session in sessions) { session.Dispose(); }
        return transcript;
    }

    [Fact]
    public void TwoWorldsWhoseHiddenStateIsEqualCompareEqualAtEveryDoor() {
        var left = Run(definition: Document(pub: 3, secret: 7));
        var right = Run(definition: Document(pub: 3, secret: 7));

        Assert.Contains(left.Entries, entry => (entry.Principal == "session:replica") && (entry.Door == "deliver.definition"));
        Assert.Contains(left.Entries, entry => entry.Door.StartsWith(value: "query.", comparisonType: StringComparison.Ordinal));
        Assert.Empty(collection: WorldDisclosureComparison.Compare(left: left, right: right));
    }
    [Fact]
    public void AWorldThatDiffersInWhatASessionReadsIsToldApartAtTheDoorThatDeliveredIt() {
        var leaks = WorldDisclosureComparison.Compare(left: Run(definition: Document(pub: 3, secret: 7)), right: Run(definition: Document(pub: 4, secret: 7)));
        var primer = Assert.Single(collection: leaks, predicate: leak => (leak.Principal == "session:replica") && (leak.Door == "deliver.definition"));

        Assert.Equal(expected: 0, actual: primer.Index);
        Assert.Contains(expectedSubstring: "document $", actualString: primer.Where, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void TheFuzzerDrawsTheSameInputsFromTheSameSeed() {
        var first = WorldDisclosureFuzzer.Draw(count: 32, from: 4UL, seed: 9UL, until: 40UL, verbs: WorldDisclosureSession.Actions, words: Words);

        Assert.Equal(expected: first, actual: WorldDisclosureFuzzer.Draw(count: 32, from: 4UL, seed: 9UL, until: 40UL, verbs: WorldDisclosureSession.Actions, words: Words));
        Assert.NotEqual(expected: first, actual: WorldDisclosureFuzzer.Draw(count: 32, from: 4UL, seed: 10UL, until: 40UL, verbs: WorldDisclosureSession.Actions, words: Words));
        Assert.All(collection: first, action: input => Assert.InRange(actual: input.Tick, high: 40UL, low: 4UL));
    }
}
