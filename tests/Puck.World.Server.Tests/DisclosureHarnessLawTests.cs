using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>The noninterference harness's own laws, over in-process pairs of worlds: two worlds whose hidden state is
/// equal compare equal for every recipient, and two that differ in anything a recipient receives (a value, a vector's
/// components, the order of its events across doors, the tick an answer arrives at, the text of an echoed refusal) are
/// told apart; a run whose setup or admission failed is invalid rather than judged. The harness's verdicts on real
/// fixtures come from <c>puck disclosure</c>, which boots them through the real executable.</summary>
public sealed class DisclosureHarnessLawTests {
    private static readonly string[] Words = ["hand", "pub", "secret", "nothing", "0", "1", "7"];
    private static readonly Dictionary<string, IReadOnlyList<string>> Slots = new() {
        ["row"] = ["hand", "nothing"],
        ["key"] = ["pub", "secret", "nothing"],
        ["value"] = ["0", "7"],
        ["transform-json"] = ["{\"$type\":\"transfer\",\"from\":\"hand\",\"to\":\"hand\",\"selector\":\"Key\",\"key\":\"secret\"}", "{\"to\":\"hand\"}"],
    };

    private static WorldAdmissionEntry Observer(string domain, WorldDisclosureTier tier) => new(Disclosure: tier, Domain: domain, Mode: WorldAdmissionTrustMode.FederatedAuthority,
        Grants: [new WorldAdmissionGrant(Budget: 64, Capability: WorldCapability.Observe, Subject: GrantSubject.All)]);
    private static WorldDefinition Document(long pub, long secret, bool hand = true) => Fixtures.BuildDocument() with {
        Admission = [Observer(domain: "replica", tier: WorldDisclosureTier.Replica), Observer(domain: "presentation", tier: WorldDisclosureTier.Presentation)],
        StateRaw = new WorldStateSection(World: (hand ? [new WorldStateRow(
            Name: CellName.Parse(candidate: "hand"), Kind: CellKind.Int,
            Visibility: new StateVisibility(Hidden: HiddenCells.Omit),
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "pub"), Value: CellValue.Int(value: pub)),
                new StateCell(Key: CellName.Parse(candidate: "secret"), Value: CellValue.Int(value: secret),
                    Visibility: new StateVisibility(Readers: ["seat1"])),
            ])] : [])),
    };
    private static WorldDisclosureTranscript Transcript(WorldFixture fixture) => new(clock: () => fixture.Server.CompletedTick);

    // One world of a pair: a replica and a presentation session observe it while each performs the same drawn actions.
    private static WorldDisclosureTranscript Run(WorldDefinition definition, Action<WorldFixture, ulong>? atTick = null, params string[] sessions) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var transcript = Transcript(fixture: fixture);
        var observed = ((sessions.Length == 0) ? ["replica", "presentation"] : sessions).Select(selector: authority =>
            WorldDisclosureSession.Observe(seed: 5UL, server: fixture.Server, sourceAuthority: authority, transcript: transcript)).ToArray();
        var actions = observed.Select(selector: (_, index) => WorldDisclosureFuzzer.Draw(count: 12, from: 1UL, seed: (5UL + (ulong)index), slots: Slots, until: 10UL,
            verbs: WorldDisclosureSession.Actions, words: Words)).ToArray();

        for (var tick = 1UL; (tick <= 12UL); tick++) {
            fixture.Step();
            atTick?.Invoke(arg1: fixture, arg2: tick);
            for (var index = 0; (index < observed.Length); index++) {
                foreach (var input in actions[index].Where(predicate: input => (input.Tick == tick))) { observed[index].Act(words: input.Words); }
            }
        }
        foreach (var session in observed) { session.Dispose(); }
        return transcript;
    }

    [Fact]
    public void TwoWorldsWhoseHiddenStateIsEqualCompareEqualForEveryRecipient() {
        var left = Run(definition: Document(pub: 3, secret: 7));
        var right = Run(definition: Document(pub: 3, secret: 7));

        Assert.Contains(left.Events, entry => (entry.Principal == "session:replica") && (entry.Door == "deliver.definition"));
        Assert.Contains(left.Events, entry => entry.Door.StartsWith(value: "query.", comparisonType: StringComparison.Ordinal));
        Assert.Contains(left.Events, entry => entry.Door.StartsWith(value: WorldDisclosureSession.OutcomeDoor, comparisonType: StringComparison.Ordinal));
        Assert.Empty(collection: left.Invalid);
        Assert.Empty(collection: WorldDisclosureComparison.Compare(left: left, right: right));
        Assert.Equal(expected: WorldDisclosureOutcome.Pass, actual: WorldDisclosureVerdict.Judge(worlds: [("control", right), ("base", left), ("same", Run(definition: Document(pub: 3, secret: 7)))]).Outcome);
    }
    [Fact]
    public void AWorldThatDiffersInWhatASessionReadsIsToldApartAtTheDoorThatDeliveredIt() {
        var leaks = WorldDisclosureComparison.Compare(left: Run(definition: Document(pub: 3, secret: 7)), right: Run(definition: Document(pub: 4, secret: 7)));
        var replica = Assert.Single(collection: leaks, predicate: leak => (leak.Principal == "session:replica"));
        var primer = Assert.Single(collection: replica.Doors, predicate: door => (door.Door == "deliver.definition"));

        Assert.Equal(expected: 0, actual: primer.Index);
        Assert.Contains(expectedSubstring: "document $", actualString: replica.Where, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void TwoWorldsThatDifferOnlyInAVectorCellsComponentsAreToldApart() {
        static WorldDisclosureTranscript Written(sbyte component) => Run(atTick: (fixture, tick) => {
            if (tick != 3UL) { return; }
            var components = new sbyte[8];

            components[0] = 64;
            components[1] = component;
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Key: "m", Kind: WorldDocumentWriteKind.Set, Principal: Principal.Console, Row: "memories",
                Value: 0L, Vector: StateVector.CreateUnchecked(components: components)));
        }, definition: Fixtures.BuildDocument() with {
            Admission = [Observer(domain: "replica", tier: WorldDisclosureTier.Replica)],
            StateRaw = new WorldStateSection(Spaces: [new StateSpace(dimensions: 8, model: "test-model", name: CellName.Parse(candidate: "lore"), revision: "1")],
                World: [new WorldStateRow(Capacity: 4, Kind: CellKind.Vector, Name: CellName.Parse(candidate: "memories"), Space: "lore")]),
        }, sessions: ["replica"]);

        var leak = Assert.Single(collection: WorldDisclosureComparison.Compare(left: Written(component: 1), right: Written(component: 2)));

        // The snapshot's field delta alone carries the value: the document deliveries agree.
        Assert.Equal(expected: WorldDisclosureSession.SnapshotDoor, actual: Assert.Single(collection: leak.Doors).Door);
    }
    [Fact]
    public void TwoWorldsThatDifferOnlyInTheOrderOfOneRecipientsEventsAcrossDoorsAreToldApart() {
        static WorldDisclosureTranscript Received(bool definitionFirst) {
            var transcript = new WorldDisclosureTranscript(clock: static () => 20UL);

            if (definitionFirst) { transcript.RecordText(door: "deliver.definition", principal: "session:replica", text: "A"); }
            transcript.RecordBytes(door: WorldDisclosureSession.SnapshotDoor, payload: [1, 2, 3], principal: "session:replica");
            if (!definitionFirst) { transcript.RecordText(door: "deliver.definition", principal: "session:replica", text: "A"); }
            return transcript;
        }
        var leak = Assert.Single(collection: WorldDisclosureComparison.Compare(left: Received(definitionFirst: true), right: Received(definitionFirst: false)));

        Assert.Equal(expected: 0, actual: leak.Index);
        Assert.Empty(collection: leak.Doors);
    }
    [Fact]
    public void TwoWorldsThatDifferOnlyInTheTickAConsoleAnswerArrivesAtAreToldApart() {
        static WorldDisclosureTranscript Answered(ulong tick) {
            var transcript = new WorldDisclosureTranscript(clock: () => tick);

            transcript.RecordResponse(line: "world.state hand", principal: "seat2", result: new CommandResult(Output: "hand int 1/4"));
            return transcript;
        }
        var leak = Assert.Single(collection: WorldDisclosureComparison.Compare(left: Answered(tick: 25UL), right: Answered(tick: 26UL)));

        Assert.Equal(expected: 25UL, actual: leak.Tick);
        Assert.Contains(expectedSubstring: "tick 25 against 26", actualString: leak.Where, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void TwoWorldsThatDifferOnlyInTheTextOfAnEchoedRefusalAreToldApart() {
        static WorldDisclosureTranscript Refused(bool hand) {
            using var fixture = Fixtures.FreshServer(definition: Document(hand: hand, pub: 3, secret: 7));
            var seat = Principal.Seat(slot: 1);
            var transcript = Transcript(fixture: fixture);

            fixture.Server.Grant(actor: Principal.Console, grant: new WorldGrant(Budget: 16, Capability: WorldCapability.Mutate, Exclusive: false,
                Grantee: seat, KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.State), Subject: GrantSubject.Section(section: WorldSection.State)));
            using (WorldDisclosureEchoTap.Attach(observed: new Dictionary<Principal, string> { [seat] = "seat2" }, server: fixture.Server, transcript: transcript)) {
                fixture.Step();
                fixture.Server.EnqueueMutation(mutation: new WorldMutation.RemoveStateCell(Key: "ghost", Principal: seat, Row: "hand"));
                fixture.Step();
            }
            return transcript;
        }
        var (left, right) = (Refused(hand: true), Refused(hand: false));
        var leak = Assert.Single(collection: WorldDisclosureComparison.Compare(left: left, right: right));

        Assert.Equal(expected: "seat2", actual: leak.Principal);
        Assert.All(collection: [left, right], action: transcript => Assert.Contains(transcript.Events, entry => (entry.Door == "echo") &&
            WorldDisclosureComparison.Render(entry: entry).StartsWith(value: "kind=Mutation rejected=True", comparisonType: StringComparison.Ordinal)));
        Assert.Equal(expected: "echo", actual: Assert.Single(collection: leak.Doors).Door);
    }
    [Fact]
    public void AVariantWhoseSetupIsRefusedIsInvalidRatherThanJudged() {
        static WorldDisclosureTranscript Variant(bool refused) {
            using var fixture = Fixtures.FreshServer(definition: Document(pub: 3, secret: 7));
            var transcript = Transcript(fixture: fixture);

            using (WorldDisclosureEchoTap.Attach(observed: new Dictionary<Principal, string>(), server: fixture.Server, transcript: transcript)) {
                fixture.Server.EnqueueMutation(mutation: new WorldMutation.RemoveStateCell(Key: (refused ? "ghost" : "secret"), Principal: Principal.Console, Row: (refused ? "nothing" : "hand")));
                fixture.Step();
            }
            return transcript;
        }
        var verdict = WorldDisclosureVerdict.Judge(worlds: [("control", Variant(refused: false)), ("base", Variant(refused: false)), ("hidden", Variant(refused: true))]);

        Assert.Equal(expected: WorldDisclosureOutcome.Invalid, actual: verdict.Outcome);
        Assert.Contains(verdict.Invalid, reason => reason.StartsWith(value: "hidden: an operator act was refused", comparisonType: StringComparison.Ordinal));
    }
    [Fact]
    public void ASessionTheWorldDoesNotAdmitLeavesTheRunInvalid() {
        var transcript = Run(definition: Document(pub: 3, secret: 7), sessions: ["replica", "stranger"]);

        Assert.Contains(transcript.Invalid, reason => reason.StartsWith(value: "session:stranger was not admitted", comparisonType: StringComparison.Ordinal));
    }
    [Fact]
    public void TheFuzzerDrawsTheSameInputsFromTheSameSeedInEachVerbsForms() {
        static string[] Drawn(ulong seed) => [.. WorldDisclosureFuzzer.Draw(count: 64, from: 4UL, seed: seed, slots: Slots, until: 40UL, verbs: WorldDisclosureSession.Actions, words: Words)
            .Select(selector: static input => $"{input.Tick}:{input.Text}")];
        var first = WorldDisclosureFuzzer.Draw(count: 64, from: 4UL, seed: 9UL, slots: Slots, until: 40UL, verbs: WorldDisclosureSession.Actions, words: Words);

        Assert.Equal(expected: Drawn(seed: 9UL), actual: Drawn(seed: 9UL));
        Assert.NotEqual(expected: Drawn(seed: 9UL), actual: Drawn(seed: 10UL));
        Assert.All(collection: first, action: input => Assert.InRange(actual: input.Tick, high: 40UL, low: 4UL));
        Assert.Contains(first, input => (input.Words[0] == "transform") && (input.Words.Count == 2) && Slots["transform-json"].Contains(value: input.Words[1]));
        Assert.Contains(first, input => (input.Words[0] == "guarded") && (input.Words.Count == 7));
    }
    [Fact]
    public void AVerbsUsageIsReadFromItsFormTable() {
        var forms = CommandForms.Of("world.state.cell.set", "<row> <key> <value> [add]", "<row> <key> <text...>", "<row> <key> <base64url-vector>");

        Assert.Equal(expected: ["<row> <key> <value> [add]", "<row> <key> <text...>", "<row> <key> <base64url-vector>"], actual: forms.Usages.Select(selector: static form => form.ToString()));
        Assert.Equal(expected: ["[row] [key]"], actual: CommandForms.Of("world.state", "[row] [key]").Usages.Select(selector: static form => form.ToString()));
    }
}
