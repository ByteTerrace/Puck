using Puck.Commands;
using Puck.Assets.Documents;
using Puck.State.Rules;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>CONTRACT UNDER TEST: only a rule's own effect writes a verdict row. The effect door stamps the firing's
/// tick into the row's reserved <c>$firedTick</c> cell, so a status that moved without one moved through another
/// door; a failing verdict is sticky, so the export carries the first failing tick and the values the gate saw
/// then; and the cell-mutation door refuses the write by the one text the boot loader refuses an authored pass
/// with.</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class WorldVerdictDoorLawTests {
    private const string Counter = "counter";
    private const string Flag = "flag";
    private const string Verdict = "answer";
    private const string Witness = "answerSeen";

    [Fact]
    public void ObserveWitnessWritesStampTheirEvidenceAndFreezeAfterFailure() {
        var source = new WorldStateRow(Name: CellName.Parse(candidate: "truth"), Kind: CellKind.Bool,
            Domain: new StateDomain.CellsOf("map"),
            Cells: [new StateCell(Key: CellName.Parse(candidate: "0"), Value: CellValue.Bool(value: true))]);
        var definition = new WorldDefinition(StateRaw: new WorldStateSection(
            Lattices: [new LatticeTopology.Grid("map", new DocumentVector3(x: 0f, y: 0f, z: 0f), 1f, 1, 1)],
            World: [source, source with { Name = CellName.Parse(candidate: "visible") },
                source with { Name = CellName.Parse(candidate: Witness),
                    Cells = [new StateCell(Key: CellName.Parse(candidate: "0"), Value: CellValue.Bool(value: false))],
                    Knowledge = new StateKnowledge(Source: "truth", Mask: "visible"), Witness = CellName.Parse(candidate: Verdict) },
                new WorldStateRow(Name: CellName.Parse(candidate: Verdict), Kind: CellKind.Int,
                    Cells: [new StateCell(Key: CellName.Parse(candidate: "ok"), Value: CellValue.Int(value: 0L))],
                    Verdict: new WorldVerdictTrait(Gate: "observed truth", Status: CellName.Parse(candidate: "ok"))),
            ])) with {
            Rules = [new WorldRule(Name: CellName.Parse(candidate: "observe"),
                Effects: [new ActionEffect.TransformState(Transform: new StateTransform.Observe(Row: Witness))])],
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason), userMessage: reason);
        var arena = new StateArena(catalog: definition.StateCatalog, section: definition.StateRaw, time: ArenaTime.Origin);
        var host = new WorldArenaHost(arena: arena, sites: WorldDrawSites.Of(catalog: arena.Catalog), generators: null,
            ticksPerSecond: 30, dynamics: null, documentSeed: 0UL, instanceIdentity: "observe",
            verdicts: WorldVerdictStamp.From(definition: definition, catalog: arena.Catalog));
        var effects = WorldFactsCompiler.CompileAll(definition: definition)[0].Effects;
        var evaluator = new RuleEvaluator(host: host);

        Assert.True(condition: arena.Catalog.TryResolve(lane: StateLane.Document, name: CellName.Parse(candidate: Verdict), handle: out var verdict));
        Assert.True(condition: arena.Catalog.TryResolve(lane: StateLane.Document, name: CellName.Parse(candidate: Witness), handle: out var witness));
        Assert.True(condition: arena.Catalog.TryResolve(lane: StateLane.Document, name: source.Name, handle: out var truth));
        host.Advance(engineTick: 1UL, tick: 1UL);

        Assert.Equal(expected: RuleOutcome.Fired, actual: evaluator.FireEffects(applied: out _, effects: effects, ruleName: "observe", stepTicks: 1UL, tick: 1UL));
        Assert.Equal(expected: 1L, actual: arena.Read(rowOrdinal: verdict.Ordinal, key: arena.Keys.Intern(name: WorldVerdict.FiredTickKey)).AsInt);
        Assert.True(condition: arena.TryWrite(rowOrdinal: verdict.Ordinal, key: arena.Keys.Intern(name: CellName.Parse(candidate: "ok")), operand: WorldVerdict.Fail, write: StateWriteKind.Set, reason: out _));
        Assert.True(condition: arena.TryWriteBoardCell(rowOrdinal: truth.Ordinal, cell: 0, value: 0L, write: StateWriteKind.Set, reason: out _));
        host.Advance(engineTick: 2UL, tick: 2UL);

        Assert.Equal(expected: RuleOutcome.Fired, actual: evaluator.FireEffects(applied: out _, effects: effects, ruleName: "observe", stepTicks: 1UL, tick: 2UL));
        Assert.True(condition: arena.TryReadBoardCell(rowOrdinal: witness.Ordinal, cell: 0, value: out var seen));
        Assert.Equal(actual: seen, expected: 1L);
        Assert.Equal(expected: 1L, actual: arena.ObservationAt(rowOrdinal: witness.Ordinal, position: 0)?.Tick);
        Assert.Equal(expected: 1L, actual: arena.Read(rowOrdinal: verdict.Ordinal, key: arena.Keys.Intern(name: WorldVerdict.FiredTickKey)).AsInt);
        Assert.Empty(collection: evaluator.Diagnostics());
    }
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [Theory]
    public void WitnessTransformsStampFreezeAndRewindAsOneFiring(bool liveEnd, int mode) {
        // mode: ordinary firing, settled failure, stamp refusal after admission.
        var domain = new StateDomain.KeysOf(Row: CellName.Parse(candidate: "tokens"), Ordered: true);
        var definition = new WorldDefinition().WithWorldState(rows: [
            new WorldStateRow(Name: CellName.Parse(candidate: "tokens"), Kind: CellKind.Int,
                Cells: [new StateCell(Key: CellName.Parse(candidate: "a"), Value: CellValue.Int(value: 1L))]),
            new WorldStateRow(Name: CellName.Parse(candidate: "from"), Kind: CellKind.Bool, Domain: domain,
                Cells: [new StateCell(Key: CellName.Parse(candidate: "a"), Value: CellValue.Bool(value: true))]),
            new WorldStateRow(Name: CellName.Parse(candidate: Witness), Kind: CellKind.Bool, Domain: domain,
                Capacity: 2, Cells: [], Witness: CellName.Parse(candidate: Verdict)),
            new WorldStateRow(Name: CellName.Parse(candidate: Verdict), Kind: CellKind.Int, Capacity: 2,
                Cells: ((mode == 1)
                    ? [new StateCell(Key: CellName.Parse(candidate: "ok"), Value: CellValue.Int(value: WorldVerdict.Fail)),
                        new StateCell(Key: WorldVerdict.FiredTickKey, Value: CellValue.Int(value: 1L))]
                    : [new StateCell(Key: CellName.Parse(candidate: "ok"), Value: CellValue.Int(value: 0L))]),
                Verdict: new WorldVerdictTrait(Gate: "the token moved", Status: CellName.Parse(candidate: "ok"))),
        ]) with {
            Rules = [new WorldRule(Name: CellName.Parse(candidate: "transfer"), Zones: [Witness], Effects: [
                new ActionEffect.SetState(State: "tokens", Key: "a", Value: 8L),
                new ActionEffect.TransformState(Transform: new StateTransform.Transfer(From: "from",
                    To: (liveEnd ? "$zones[(tokens[a] - 8)]" : Witness), Selector: ZoneSelector.First)),
            ])],
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: definition, reason: out var reason, neighbours: null), userMessage: reason);
        var rules = WorldFactsCompiler.CompileAll(definition: definition);
        var arena = new StateArena(catalog: definition.StateCatalog, section: definition.StateRaw, time: ArenaTime.Origin);

        Assert.True(condition: arena.Catalog.TryResolve(lane: StateLane.Document, name: CellName.Parse(candidate: Verdict), handle: out var verdict));
        if (mode == 2) {
            Assert.True(condition: arena.TryMint(rowOrdinal: verdict.Ordinal, name: CellName.Parse(candidate: "occupied"),
                value: CellValue.Int(value: 0L), key: out _, reason: out _));
        }
        var host = new WorldArenaHost(arena: arena, sites: WorldDrawSites.Of(catalog: arena.Catalog), generators: null,
            ticksPerSecond: 30, dynamics: null, documentSeed: 0UL, instanceIdentity: "effect-door",
            verdicts: WorldVerdictStamp.From(definition: definition, catalog: arena.Catalog));

        host.Advance(engineTick: 5UL, tick: 5UL);
        var evaluator = new RuleEvaluator(host: host);
        var before = arena.ComputeHash();

        var outcome = evaluator.FireEffects(effects: rules[0].Effects, ruleName: "transfer", tick: 5UL, stepTicks: 1UL, applied: out var moved);

        Assert.Equal(actual: outcome, expected: ((mode == 2) ? RuleOutcome.Refused : RuleOutcome.Fired));
        var rows = arena.ToRows();

        Assert.Equal(expected: ((mode == 0) ? 1 : 0), actual: (rows.Single(predicate: row => (row.Name == Witness)).Cells?.Count ?? 0));
        Assert.Equal(expected: ((mode == 0) ? 0 : 1), actual: (rows.Single(predicate: row => (row.Name == "from")).Cells?.Count ?? 0));
        Assert.Equal(expected: ((mode == 0) ? 5L : ((mode == 1) ? 1L : (long?)null)),
            actual: StateRows.FindCell(cells: rows.Single(predicate: row => (row.Name == Verdict)).Cells, key: WorldVerdict.FiredTickKey)?.Value.Raw);
        if (mode == 2) {
            Assert.False(condition: moved);
            Assert.Equal(expected: before, actual: arena.ComputeHash());
            Assert.Single(collection: evaluator.Diagnostics());
        } else {
            Assert.Empty(collection: evaluator.Diagnostics());
        }
    }

    private static WorldDefinition Document(long passWhen, long failWhen) => (Fixtures.BuildDocument().WithWorldState(rows: [
        new WorldStateRow(
            Name: CellName.Parse(candidate: Counter),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: CellName.Parse(candidate: "n"),
                    Value: CellValue.Int(value: 0L)
                )]
        ),
        new WorldStateRow(
            Name: CellName.Parse(candidate: Verdict),
            Kind: CellKind.Int,
            Cells: [
                new StateCell(
                    Key: CellName.Parse(candidate: "ok"),
                    Value: CellValue.Int(value: 0L)
                ),
                new StateCell(
                    Key: CellName.Parse(candidate: "saw"),
                    Value: CellValue.Int(value: 0L)
                ),
            ],
            Verdict: new WorldVerdictTrait(
                Gate: "the counter reached its mark",
                Status: CellName.Parse(candidate: "ok")
            )
        ),
        new WorldStateRow(
            Name: CellName.Parse(candidate: Flag),
            Kind: CellKind.Bool,
            Cells: [new StateCell(
                    Key: CellName.Parse(candidate: "f"),
                    Value: CellValue.Bool(value: true)
                )]
        ),
        new WorldStateRow(
            Name: CellName.Parse(candidate: Witness),
            Kind: CellKind.Bool,
            Cells: [new StateCell(
                    Key: CellName.Parse(candidate: "f"),
                    Value: CellValue.Bool(value: false)
                )],
            Witness: CellName.Parse(candidate: Verdict)
        ),
    ]) with {
        Rules = [
            Decide(
                name: "verdict-fails",
                status: WorldVerdict.Fail,
                when: failWhen
            ),
            Decide(
                name: "verdict-passes",
                status: WorldVerdict.Pass,
                when: passWhen
            ),
        ],
    });
    private static WorldRule Decide(string name, long when, long status) => new(
        Name: CellName.Parse(candidate: name),
        Gate: new ActionPredicate.CompareState(
            Comparison: ExpressionOp.Equal,
            Key: "n",
            State: Counter,
            Value: when
        ),
        Mode: ActionTriggerMode.Level,
        Effects: [
            new ActionEffect.SetState(
                Key: "ok",
                State: Verdict,
                Value: status
            ),
            new ActionEffect.SetState(
                FromKey: "n",
                FromState: Counter,
                Key: "saw",
                State: Verdict
            ),
            new ActionEffect.SetState(
                FromKey: "f",
                FromState: Flag,
                Key: "f",
                State: Witness
            ),
        ]
    );
    private static long? Seen(WorldFixture fixture) => StateRows.FindCell(
        cells: WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.ExportDefinition().State,
            name: Witness
        )!.Cells,
        key: CellName.Parse(candidate: "f")
    )?.Value.Raw;
    private static void Lower(WorldFixture fixture) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Key: "f",
        Kind: WorldDocumentWriteKind.Set,
        Principal: Principal.Console,
        Row: Flag,
        Value: 0L
    ));
    private static long? Cell(WorldFixture fixture, string key) => StateRows.FindCell(
        cells: WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.ExportDefinition().State,
            name: Verdict
        )!.Cells,
        key: CellName.Parse(candidate: key)
    )?.Value.Raw;
    private static void Set(WorldFixture fixture, long value) => fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
        Key: "n",
        Kind: WorldDocumentWriteKind.Set,
        Principal: Principal.Console,
        Row: Counter,
        Value: value
    ));

    [Fact]
    public void EveryVerdictFiringIsStampedWithoutGrowingTheArenaDuringTheTick() {
        const int Count = 64;
        var rows = Enumerable.Range(count: Count, start: 0).Select(selector: index => new WorldStateRow(
            Name: CellName.Parse(candidate: $"answer{index}"),
            Kind: CellKind.Int,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "ok"), Value: CellValue.Int(value: 0L))],
            Verdict: new WorldVerdictTrait(Gate: "the rule fired", Status: CellName.Parse(candidate: "ok"))
        )).ToArray();
        var definition = Fixtures.BuildDocument().WithWorldState(rows: [
            // Unrelated storage makes each runtime reservation copy the composed world's whole arena.
            new WorldStateRow(Name: CellName.Parse(candidate: "padding"), Kind: CellKind.Int, Capacity: 4096),
            .. rows,
        ]) with {
            Rules = rows.Select(selector: row => new WorldRule(
                Name: row.Name,
                Gate: new ActionPredicate.CompareState(Comparison: ExpressionOp.Equal, State: row.Name.Value, Key: "ok", Value: 0L),
                Effects: [new ActionEffect.SetState(State: row.Name.Value, Key: "ok", Value: WorldVerdict.Pass)]
            )).ToArray(),
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();
        var exported = fixture.Server.ExportDefinition();

        foreach (var row in rows) {
            var cells = WorldDefinitionRows.FindStateRow(rows: exported.State, name: row.Name.Value)!.Cells;

            Assert.Equal(expected: WorldVerdict.Pass, actual: StateRows.FindCell(cells: cells, key: CellName.Parse(candidate: "ok"))!.Value.Raw);
            Assert.True(condition: (StateRows.FindCell(cells: cells, key: WorldVerdict.FiredTickKey)?.Value.Raw > 0L), userMessage: $"{row.Name} passed without its firing stamp");
        }
        Assert.Equal(expected: 0L, actual: fixture.Server.Arena.ReservationWorkCharged);
    }
    [Fact]
    public void AStampRefusalRewindsTheVerdictStatusAndItsWitnesses() {
        var definition = Document(failWhen: 9L, passWhen: 0L);

        definition = definition.WithWorldState(rows: definition.AuthoredState.Select(selector: row => ((row.Name.Value == Verdict)
            ? row with { Capacity = 3 }
            : row)).ToArray());
        using var fixture = Fixtures.FreshServer(definition: definition);

        Assert.True(condition: fixture.Server.Definition.StateCatalog.TryResolve(lane: StateLane.Document, name: CellName.Parse(candidate: Verdict), handle: out var handle));
        // Occupy the remaining slot through raw storage after admission to exercise the effect door's refusal.
        Assert.True(condition: fixture.Server.Arena.TryMint(rowOrdinal: handle.Ordinal, name: CellName.Parse(candidate: "occupied"), value: CellValue.Int(value: 0L), key: out _, reason: out _));

        fixture.Step();

        Assert.Equal(expected: WorldVerdict.NotEvaluated, actual: Cell(fixture: fixture, key: "ok"));
        Assert.Null(value: Cell(fixture: fixture, key: WorldVerdict.FiredTickKey.Value));
        Assert.Equal(expected: 0L, actual: Seen(fixture: fixture));
    }
    [Fact]
    public void AVerdictNoRuleWroteCarriesNoFiringStampAndAFiringStampsItsOwnTick() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            failWhen: 9L,
            passWhen: 5L
        ));

        fixture.Step();
        fixture.Step();

        Assert.Null(value: Cell(
            fixture: fixture,
            key: WorldVerdict.FiredTickKey.Value
        ));
        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "ok"
            ),
            expected: WorldVerdict.NotEvaluated
        );

        Set(
            fixture: fixture,
            value: 5L
        );
        fixture.Step();
        fixture.Step();

        var stamp = Cell(
            fixture: fixture,
            key: WorldVerdict.FiredTickKey.Value
        );

        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "ok"
            ),
            expected: WorldVerdict.Pass
        );
        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "saw"
            ),
            expected: 5L
        );
        Assert.NotNull(value: stamp);
        Assert.True(
            condition: (stamp!.Value > 0L),
            userMessage: "a firing stamped tick 0, which reads as never evaluated"
        );
        Assert.True(
            condition: (((ulong)stamp.Value) < fixture.Server.NextInputTick),
            userMessage: "the stamp names a tick the run has not completed"
        );
    }
    [Fact]
    public void AVerdictThatEverFiredFailStaysFailedWithTheValuesItsGateSawThen() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            failWhen: 3L,
            passWhen: 7L
        ));

        Set(
            fixture: fixture,
            value: 3L
        );
        fixture.Step();
        fixture.Step();

        var settled = Cell(
            fixture: fixture,
            key: WorldVerdict.FiredTickKey.Value
        );

        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "ok"
            ),
            expected: WorldVerdict.Fail
        );
        Assert.NotNull(value: settled);
        Assert.Equal(
            actual: Seen(fixture: fixture),
            expected: 1L
        );

        Lower(fixture: fixture);
        Set(
            fixture: fixture,
            value: 7L
        );
        fixture.Step();
        fixture.Step();
        fixture.Step();

        Assert.Equal(
            actual: Seen(fixture: fixture),
            expected: 1L
        );

        // The control: the pass rule's gate is open now, and a verdict that had NOT failed would read Pass here.
        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "ok"
            ),
            expected: WorldVerdict.Fail
        );
        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "saw"
            ),
            expected: 3L
        );
        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: WorldVerdict.FiredTickKey.Value
            ),
            expected: settled
        );
    }
    [Fact]
    public void AVerdictThatNeverFailedReachesPassOnTheSameSchedule() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            failWhen: 99L,
            passWhen: 7L
        ));

        Set(
            fixture: fixture,
            value: 3L
        );
        fixture.Step();
        fixture.Step();
        Lower(fixture: fixture);
        Set(
            fixture: fixture,
            value: 7L
        );
        fixture.Step();
        fixture.Step();
        fixture.Step();

        Assert.Equal(
            actual: Cell(
                fixture: fixture,
                key: "ok"
            ),
            expected: WorldVerdict.Pass
        );
        Assert.Equal(
            actual: Seen(fixture: fixture),
            expected: 0L
        );
    }
    [Fact]
    public void TheCellMutationDoorRefusesAWriteToAWitnessByName() {
        var previous = Console.Error;
        var captured = new StringWriter();

        try {
            Console.SetError(newError: captured);

            using var fixture = Fixtures.FreshServer(definition: Document(
                failWhen: 9L,
                passWhen: 5L
            ));

            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
                Key: "f",
                Kind: WorldDocumentWriteKind.Set,
                Principal: Principal.Console,
                Row: Witness,
                Value: 1L
            ));
            fixture.Step();
            fixture.Step();

            Assert.Equal(
                actual: Seen(fixture: fixture),
                expected: 0L
            );
        } finally {
            Console.SetError(newError: previous);
        }

        Assert.Contains(
            actualString: captured.ToString(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"'{Witness}' is a witness of the verdict '{Verdict}'"
        );
    }
    [Fact]
    public void TheCellMutationDoorRefusesAWriteToAVerdictRowByName() {
        var previous = Console.Error;
        var captured = new StringWriter();

        try {
            Console.SetError(newError: captured);

            using var fixture = Fixtures.FreshServer(definition: Document(
                failWhen: 9L,
                passWhen: 5L
            ));

            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
                Key: "ok",
                Kind: WorldDocumentWriteKind.Set,
                Principal: Principal.Console,
                Row: Verdict,
                Value: WorldVerdict.Pass
            ));
            fixture.Step();
            fixture.Step();

            Assert.Equal(
                actual: Cell(
                    fixture: fixture,
                    key: "ok"
                ),
                expected: WorldVerdict.NotEvaluated
            );
            Assert.Null(value: Cell(
                fixture: fixture,
                key: WorldVerdict.FiredTickKey.Value
            ));
        } finally {
            Console.SetError(newError: previous);
        }

        Assert.Contains(
            actualString: captured.ToString(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: WorldVerdict.RefuseWrite(row: new WorldStateRow(
                Name: CellName.Parse(candidate: Verdict),
                Kind: CellKind.Int,
                Verdict: new WorldVerdictTrait(
                    Gate: "the counter reached its mark",
                    Status: CellName.Parse(candidate: "ok")
                )
            ))
        );
    }
}
