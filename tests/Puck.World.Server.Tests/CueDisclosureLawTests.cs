using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Pins the rule cue disclosure gate: a cue reaches a recipient only when it holds everything the emitting
/// rule's firing depended on, as each fact declares it on its own type, and a fact that declares nothing reaches only
/// the authority. Running a world cannot show a leak, so these laws read the gate's classification and the per-seat
/// audience of the in-process delivery directly.</summary>
public sealed class CueDisclosureLawTests {
    // Seat 1's principal token: the hidden rows below are readable by the second local seat alone.
    private const string SecondSeat = "seat2";

    [Fact]
    public void APairKeyOverAHiddenRowWithholdsTheCueFromASeatThatCannotReadTheRow() {
        var definition = Document(
            state: [Hidden(name: "secret", key: "who", value: 1L), Keyed(name: "tally")],
            rules: [new WorldRule(
                Name: Name(value: "pair"),
                Effects: [
                    new ActionEffect.SetState(State: "tally", Key: "$pair:cell:secret:who:body:0", Value: 1m),
                    new WorldEffect.EmitCue(Name: "pair.cue"),
                ]
            )]
        );
        var cue = DeliverOne(definition: definition, cueName: "pair.cue");

        Assert.False(condition: cue.AdmitsSeat(seat: 0), userMessage: "seat 0 cannot read 'secret', which the pair key resolves through");
        Assert.True(condition: cue.AdmitsSeat(seat: 1), userMessage: "seat 1 reads 'secret' whole");
    }
    [Fact]
    public void APairKeyDeclaresTheRowsItsBodyReferencesReadAndTheBodies() {
        var definition = Document(
            state: [Hidden(name: "secret", key: "who", value: 1L), Keyed(name: "tally")],
            rules: [new WorldRule(
                Name: Name(value: "pair"),
                Effects: [new ActionEffect.SetState(State: "tally", Key: "$pair:cell:secret:who:body:0", Value: 1m), new WorldEffect.EmitCue(Name: "pair.cue")]
            )]
        );
        var needs = Needs(definition: definition);

        Assert.Contains(expected: RowOrdinal(definition: definition, name: "secret"), collection: needs.Rows);
        Assert.Equal(expected: WorldDisclosureReach.Bodies, actual: needs.Reach & WorldDisclosureReach.Bodies);
        Assert.Empty(collection: needs.Undeclared);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void NeighbourPerceptionReadsBodiesAndASightTestReadsTheFields(bool sight) {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(World: [Keyed(name: "observers", cells: ["1"]), Keyed(name: "appeal", cells: ["0", "1"])]),
            Rules = [new WorldRule(
                Name: Name(value: "choose"),
                Effects: [],
                ForEach: "observers",
                Decision: new WorldDecision(
                    [
                        new WorldDecisionOption(
                            Name(value: "companion"),
                            new ExpressionProgram(Instructions: [Instruction.Operand(key: "$right", name: "appeal")]),
                            [new WorldEffect.EmitCue(Name: "seen")],
                            Neighbors: new WorldDecisionNeighbors(20, 4, 3, RequiresLineOfSight: sight)
                        ),
                        new WorldDecisionOption(Name(value: "alone"), ExpressionProgram.Parse(text: "0"), []),
                    ],
                    0.01m,
                    ScoreKind: CellKind.Int
                )
            )],
        };
        var needs = Needs(definition: definition);

        Assert.True(condition: (needs.Reach & WorldDisclosureReach.Bodies) != 0, userMessage: "perception finds bodies by position");
        Assert.Equal(expected: sight, actual: (needs.Reach & WorldDisclosureReach.Fields) != 0);
    }
    [InlineData("$los:body:0:body:1", true)]
    [InlineData("$nav:body:0:hasPath", true)]
    [InlineData("$distance:body:0:body:1", false)]
    [Theory]
    public void ABodyFactThatTestsASightLineOrAPathReadsEveryFieldRegion(string fact, bool fields) {
        var definition = Document(
            state: [],
            rules: [new WorldRule(
                Name: Name(value: "sense"),
                Effects: [new WorldEffect.EmitCue(Name: "sensed")],
                Gate: new ActionPredicate.CompareState(State: fact, Comparison: ExpressionOp.GreaterOrEqual, Value: 0m)
            )]
        );
        var needs = Needs(definition: definition);

        Assert.True(condition: (needs.Reach & WorldDisclosureReach.Bodies) != 0);
        Assert.Equal(expected: fields, actual: (needs.Reach & WorldDisclosureReach.Fields) != 0);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AnIdentityFactCueReachesTheSeatsThatReadTheIdentityLane(bool hidden) {
        var lane = new WorldStateRow(Name: Name(value: WorldIdentityFactLane.RowName), Kind: CellKind.Int, Capacity: 16,
            Visibility: hidden ? new StateVisibility(Readers: [SecondSeat]) : null);
        var definition = Document(
            state: [lane],
            rules: [new WorldRule(
                Name: Name(value: "armed"),
                Effects: [new WorldEffect.EmitCue(Name: "armed.cue")],
                Gate: new ActionPredicate.CompareState(State: $"{WorldRuleFacts.IdentityPrefix}body:0:armed", Comparison: ExpressionOp.Equal, Value: 0m)
            )]
        );
        var cue = DeliverOne(definition: definition, cueName: "armed.cue");

        Assert.Equal(expected: !hidden, actual: cue.AdmitsSeat(seat: 0));
        Assert.True(condition: cue.AdmitsSeat(seat: 1));
    }
    [Fact]
    public void AMachineMemoryCueDependsOnItsScreenAndNeverDefaultsToTheAuthority() {
        var definition = Document(
            state: [],
            rules: [new WorldRule(
                Name: Name(value: "coin"),
                Effects: [new WorldEffect.EmitCue(Name: "coin")],
                Gate: new ActionPredicate.CompareState(State: $"{WorldRuleFacts.MachinePrefix}0:16", Comparison: ExpressionOp.GreaterOrEqual, Value: 0m)
            )]
        );
        var needs = Needs(definition: definition);

        Assert.Equal(expected: [0], actual: needs.Screens);
        Assert.Empty(collection: needs.Undeclared);
    }
    [Fact]
    public void AHostFactWithNoDeclarationReachesOnlyTheAuthority() {
        var needs = new WorldDisclosureNeeds();

        WorldRuleDisclosure.Classify(fact: new UndeclaredFact(), into: needs);

        Assert.Equal(expected: [nameof(UndeclaredFact)], actual: needs.Undeclared);
    }

    private static WorldGameplayCue DeliverOne(WorldDefinition definition, string cueName) {
        using var fixture = Fixtures.FreshServer(definition: definition);

        for (var slot = 0; slot < 2; slot++) {
            Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
                Principal: Principal.Seat(slot: slot),
                Slot: slot,
                IdentityName: null,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
        }
        var sink = new RecordingSink();
        using var delivery = fixture.Server.AttachLocalPresentation(sink: sink);
        fixture.Step();

        return Assert.Single(sink.LastCues, cue => cue.Name == cueName);
    }
    private static WorldDisclosureNeeds Needs(WorldDefinition definition) {
        var needs = new WorldDisclosureNeeds();

        foreach (var rule in WorldFactsCompiler.CompileAll(definition: definition)) { WorldRuleDisclosure.Collect(rule: rule, into: needs); }
        return needs;
    }
    private static int RowOrdinal(WorldDefinition definition, string name) {
        using var fixture = Fixtures.FreshServer(definition: definition);

        Assert.True(condition: fixture.Server.Arena.Catalog.TryResolve(StateLane.Document, name, out var handle));
        return handle.Ordinal;
    }
    private static WorldDefinition Document(IReadOnlyList<WorldStateRow> state, IReadOnlyList<WorldRule> rules) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: state),
        Rules = rules,
    };
    private static WorldStateRow Hidden(string name, string key, long value) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Capacity: 4,
        Cells: [new StateCell(Key: Name(value: key), Value: CellValue.Int(value: value))],
        Visibility: new StateVisibility(Readers: [SecondSeat])
    );
    private static WorldStateRow Keyed(string name, IReadOnlyList<string>? cells = null) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Capacity: 8,
        Cells: [.. (cells ?? []).Select(key => new StateCell(Key: Name(value: key), Value: CellValue.Int(value: 0L)))]
    );
    private static CellName Name(string value) => CellName.Parse(candidate: value);

    // A host fact whose type carries no disclosure declaration.
    private sealed class UndeclaredFact : ICompiledFact {
        public FacetRef? RequiredFacet => FacetRef.Of<IWorldFacts>();
    }
}
