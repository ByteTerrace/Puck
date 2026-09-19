using Puck.Maths;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>Proves every rule read of a cell carrying a <see cref="StateDynamics"/> trait — a gate, an expression
/// operand, an arithmetic write's operand, an effect source, a reduction, a computed key, a countdown — takes the
/// stored truth rather than the follower's eased sample, while a cell under <see cref="StateAdvance"/> or
/// <see cref="StateCycle"/> still reads live.</summary>
public sealed class RuleReadsTruthLawTests {
    private static readonly DynamicsRow[] TestDynamics = [
        new(
            Damping: 1f,
            Frequency: 1f,
            Name: "ease",
            Response: 0f
        )
    ];

    private static StateSection BuildSection() => new(Rows: [
        new StateRow(
            Name: RulesFixture.Name(value: "eased"),
            Kind: CellKind.Int,
            Dynamics: new StateDynamics(Row: "ease"),
            Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "cellEased"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: [new StateCell(
                Dynamics: new StateDynamics(Row: "ease"),
                Key: RulesFixture.Name(value: "0"),
                Value: CellValue.Int(value: 0L)
            )]
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "advancing"),
            Kind: CellKind.Int,
            Advance: new StateAdvance(
                PerSecondDenominator: 1L,
                PerSecondNumerator: 10L
            ),
            Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "cycling"),
            Kind: CellKind.Int,
            Cycle: new StateCycle(
                Output: CycleOutput.Step,
                TicksPerStep: 10L
            ),
            Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "tableEased"),
            Kind: CellKind.Int,
            Dynamics: new StateDynamics(Row: "ease"),
            Capacity: 4,
            Cells: [
                new StateCell(
                    Key: RulesFixture.Name(value: "0"),
                    Value: CellValue.Int(value: 0L)
                ),
                new StateCell(
                    Key: RulesFixture.Name(value: "1"),
                    Value: CellValue.Int(value: 0L)
                )
            ]
        ),
        EvaluatorFixture.Slot(
            name: "out",
            value: 0L
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "keyRef"),
            Kind: CellKind.Int,
            Dynamics: new StateDynamics(Row: "ease"),
            Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "targetRow"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: [
                new StateCell(
                    Key: RulesFixture.Name(value: "0"),
                    Value: CellValue.Int(value: 50L)
                ),
                new StateCell(
                    Key: RulesFixture.Name(value: "1"),
                    Value: CellValue.Int(value: 75L)
                )
            ]
        ),
        new StateRow(
            Name: RulesFixture.Name(value: "countdown"),
            Kind: CellKind.Int,
            Min: 0L,
            Dynamics: new StateDynamics(Row: "ease"),
            Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
        )
    ]);
    private static CellKey Key(StateCatalog catalog, string? name = null) =>
        catalog.Keys.Intern(name: ((name is null) ? StateRow.SlotKey : RulesFixture.Name(value: name)));
    private static (ArenaEffectHost Host, RuleEvaluator Evaluator, RuleCompileContext Context, StateArena Arena) Arrange() {
        var section = BuildSection();
        var context = EvaluatorFixture.Context(section: section);
        var arena = new StateArena(
            catalog: context.Catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var host = new ArenaEffectHost(
            arena: arena,
            dynamics: TestDynamics,
            ticksPerSecond: 30
        );

        return (host, new RuleEvaluator(host: host), context, arena);
    }

    [Fact]
    public void RuleGateOverAnEasedCellReadsStoredTruth() {
        var (host, evaluator, context, arena) = Arrange();
        var easedOrdinal = EvaluatorFixture.Ordinal(
            host: host,
            row: "eased"
        );
        var slotKey = Key(catalog: arena.Catalog);

        // Retarget at tick 0: stored value becomes 100, clock kicked from 0.
        Assert.True(condition: arena.TryWriteLive(
            key: slotKey,
            operand: 100L,
            reason: out var reason,
            rowOrdinal: easedOrdinal,
            time: host.Time,
            write: StateWriteKind.Set
        ), userMessage: reason);

        var rule = RulesFixture.Rule(
            gate: EvaluatorFixture.Compare(
                comparison: ActionStateComparison.GreaterOrEqual,
                row: "eased",
                value: 100m
            ),
            name: "gateTruth",
            effects: [EvaluatorFixture.Set(
                row: "out",
                value: 1m
            )]
        );
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: [rule]
        );
        var latch = new RuleLatch();

        // Advance to tick 1: mid-ease. A truth read sees 100 and opens the gate.
        host.Advance(
            engineTick: 1UL,
            tick: 1UL
        );
        evaluator.Evaluate(
            latch: latch,
            rules: compiled,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "out"
            ),
            expected: 1L
        );
    }
    [Fact]
    public void ExpressionOperandReadingAnEasedCellReadsStoredTruth() {
        var (host, evaluator, context, arena) = Arrange();
        var easedOrdinal = EvaluatorFixture.Ordinal(
            host: host,
            row: "eased"
        );
        var slotKey = Key(catalog: arena.Catalog);

        Assert.True(condition: arena.TryWriteLive(
            key: slotKey,
            operand: 100L,
            reason: out var reason,
            rowOrdinal: easedOrdinal,
            time: host.Time,
            write: StateWriteKind.Set
        ), userMessage: reason);

        var rule = new Rule(
            Name: RulesFixture.Name(value: "exprTruth"),
            Effects: [new ActionEffect.SetState(
                Expression: RulesFixture.Program(text: "eased + 5"),
                State: "out"
            )],
            Mode: ActionTriggerMode.Level
        );
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: [rule]
        );
        var latch = new RuleLatch();

        host.Advance(
            engineTick: 1UL,
            tick: 1UL
        );
        evaluator.Evaluate(
            latch: latch,
            rules: compiled,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "out"
            ),
            expected: 105L
        );
    }
    [Fact]
    public void AddStateOnAnEasedCellComputesFromStoredTruth() {
        var (host, evaluator, context, arena) = Arrange();
        var easedOrdinal = EvaluatorFixture.Ordinal(
            host: host,
            row: "eased"
        );
        var slotKey = Key(catalog: arena.Catalog);

        // Initialize stored base to 300.
        Assert.True(condition: arena.TryWriteLive(
            key: slotKey,
            operand: 300L,
            reason: out var reason,
            rowOrdinal: easedOrdinal,
            time: host.Time,
            write: StateWriteKind.Set
        ), userMessage: reason);

        // Rule executes AddState of 10.
        var rule = RulesFixture.Rule(
            name: "addTruth",
            effects: [EvaluatorFixture.Add(
                row: "eased",
                value: 10m
            )]
        );
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: [rule]
        );
        var latch = new RuleLatch();

        // At tick 1 (mid-ease from 0 to 300), add 10 should compute from stored truth 300 -> 310.
        host.Advance(
            engineTick: 1UL,
            tick: 1UL
        );
        evaluator.Evaluate(
            latch: latch,
            rules: compiled,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "eased"
            ),
            expected: 310L
        );
    }
    [Fact]
    public void SetStateFromStateSourcedFromAnEasedCellCopiesStoredTruth() {
        var (host, evaluator, context, arena) = Arrange();
        var easedOrdinal = EvaluatorFixture.Ordinal(
            host: host,
            row: "eased"
        );
        var slotKey = Key(catalog: arena.Catalog);

        Assert.True(condition: arena.TryWriteLive(
            key: slotKey,
            operand: 250L,
            reason: out var reason,
            rowOrdinal: easedOrdinal,
            time: host.Time,
            write: StateWriteKind.Set
        ), userMessage: reason);

        var rule = RulesFixture.Rule(
            name: "copyTruth",
            effects: [new ActionEffect.SetState(
                FromState: "eased",
                State: "out"
            )]
        );
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: [rule]
        );
        var latch = new RuleLatch();

        host.Advance(
            engineTick: 1UL,
            tick: 1UL
        );
        evaluator.Evaluate(
            latch: latch,
            rules: compiled,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "out"
            ),
            expected: 250L
        );
    }
    [Fact]
    public void ReductionOverARowHoldingAnEasedCellReadsStoredTruth() {
        var (host, evaluator, context, arena) = Arrange();
        var tableOrdinal = EvaluatorFixture.Ordinal(
            host: host,
            row: "tableEased"
        );
        var k0 = Key(
            catalog: arena.Catalog,
            name: "0"
        );
        var k1 = Key(
            catalog: arena.Catalog,
            name: "1"
        );

        Assert.True(condition: arena.TryWriteLive(
            key: k0,
            operand: 100L,
            reason: out var r0,
            rowOrdinal: tableOrdinal,
            time: host.Time,
            write: StateWriteKind.Set
        ), userMessage: r0);
        Assert.True(condition: arena.TryWriteLive(
            key: k1,
            operand: 200L,
            reason: out var r1,
            rowOrdinal: tableOrdinal,
            time: host.Time,
            write: StateWriteKind.Set
        ), userMessage: r1);

        var rule = new Rule(
            Name: RulesFixture.Name(value: "reduceTruth"),
            Effects: [new ActionEffect.SetState(
                Expression: RulesFixture.Program(text: "$reduce:sum:tableEased"),
                State: "out"
            )],
            Mode: ActionTriggerMode.Level
        );
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: [rule]
        );
        var latch = new RuleLatch();

        host.Advance(
            engineTick: 1UL,
            tick: 1UL
        );
        evaluator.Evaluate(
            latch: latch,
            rules: compiled,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "out"
            ),
            expected: 300L
        );
    }
    // The other laws author the trait on the row, the way pong authors pongSpin; a keyed cell may carry its own.
    [Fact]
    public void AGateOverACellCarryingItsOwnDynamicsTraitReadsStoredTruth() {
        var (host, evaluator, context, arena) = Arrange();

        Assert.True(condition: arena.TryWriteLive(
            key: Key(
                catalog: arena.Catalog,
                name: "0"
            ),
            operand: 50L,
            reason: out var reason,
            rowOrdinal: EvaluatorFixture.Ordinal(
                host: host,
                row: "cellEased"
            ),
            time: host.Time,
            write: StateWriteKind.Set
        ), userMessage: reason);

        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: [RulesFixture.Rule(
                gate: new ActionPredicate.CompareState(
                    Comparison: ActionStateComparison.GreaterOrEqual,
                    Key: "0",
                    State: "cellEased",
                    Value: 50m
                ),
                name: "cellEasedGate",
                effects: [EvaluatorFixture.Set(
                    row: "out",
                    value: 1m
                )]
            )]
        );

        host.Advance(
            engineTick: 1UL,
            tick: 1UL
        );
        evaluator.Evaluate(
            latch: new RuleLatch(),
            rules: compiled,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "out"
            ),
            expected: 1L
        );
    }
    [Fact]
    public void ComputedKeyReadFromAnEasedCellResolvesAgainstStoredTruth() {
        var (host, evaluator, context, arena) = Arrange();
        var keyOrdinal = EvaluatorFixture.Ordinal(
            host: host,
            row: "keyRef"
        );
        var slotKey = Key(catalog: arena.Catalog);

        Assert.True(condition: arena.TryWriteLive(
            key: slotKey,
            operand: 1L,
            reason: out var reason,
            rowOrdinal: keyOrdinal,
            time: host.Time,
            write: StateWriteKind.Set
        ), userMessage: reason);

        var rule = new Rule(
            Name: RulesFixture.Name(value: "computedKeyTruth"),
            Effects: [new ActionEffect.SetState(
                Expression: RulesFixture.Program(text: "targetRow[(keyRef)]"),
                State: "out"
            )],
            Mode: ActionTriggerMode.Level
        );
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: [rule]
        );
        var latch = new RuleLatch();

        // One tick into the ease the follower still rounds to key 0, which holds 50; the truth names key 1.
        host.Advance(
            engineTick: 1UL,
            tick: 1UL
        );
        evaluator.Evaluate(
            latch: latch,
            rules: compiled,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "out"
            ),
            expected: 75L
        );
    }
    [Fact]
    public void CountdownEffectReadingAnEasedCellReadsStoredTruth() {
        var (host, evaluator, context, arena) = Arrange();
        var countdownOrdinal = EvaluatorFixture.Ordinal(
            host: host,
            row: "countdown"
        );
        var slotKey = Key(catalog: arena.Catalog);

        // Retarget countdown to 60 at tick 0.
        Assert.True(condition: arena.TryWriteLive(
            key: slotKey,
            operand: 60L,
            reason: out var reason,
            rowOrdinal: countdownOrdinal,
            time: host.Time,
            write: StateWriteKind.Set
        ), userMessage: reason);

        // Countdown effect decrements by min(current, stepTicks).
        var rule = new Rule(
            Name: RulesFixture.Name(value: "countdownStep"),
            Effects: [new ActionEffect.CountdownState(State: "countdown")],
            Mode: ActionTriggerMode.Level
        );
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: [rule]
        );
        var latch = new RuleLatch();

        // Step by 5 ticks at tick 1. Truth is 60, min(60, 5) = 5 -> countdown becomes 55.
        // If eased sample (< 5) was read, min would decrement by less than 5.
        host.Advance(
            engineTick: 1UL,
            tick: 1UL
        );
        evaluator.Evaluate(
            latch: latch,
            rules: compiled,
            stepTicks: 5UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "countdown"
            ),
            expected: 55L
        );
    }
    // The fix must not flatten a value-over-time trait to its stored base: an accumulating cell's live value is the
    // value a rule sees.
    [Fact]
    public void AGateOverAnAdvancingCellStillReadsItsLiveValue() {
        var (host, evaluator, context, _) = Arrange();
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: [RulesFixture.Rule(
                gate: EvaluatorFixture.Compare(
                    comparison: ActionStateComparison.GreaterOrEqual,
                    row: "advancing",
                    value: 10m
                ),
                name: "advancingGate",
                effects: [EvaluatorFixture.Set(
                    row: "out",
                    value: 1m
                )]
            )]
        );

        // One second of engine ticks at ten per second carries the stored 0 to 10.
        host.Advance(
            engineTick: FixedTickConversion.TicksPerSecond,
            tick: 30UL
        );
        evaluator.Evaluate(
            latch: new RuleLatch(),
            rules: compiled,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "out"
            ),
            expected: 1L
        );
    }
    // A rotating cell's stored value is its phase; the gate reads the rotation the trait carried that phase to.
    [Fact]
    public void AGateOverACyclingCellStillReadsItsLiveValue() {
        var (host, evaluator, context, _) = Arrange();
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: [RulesFixture.Rule(
                gate: EvaluatorFixture.Compare(
                    comparison: ActionStateComparison.GreaterOrEqual,
                    row: "cycling",
                    value: 1m
                ),
                name: "cyclingGate",
                effects: [EvaluatorFixture.Set(
                    row: "out",
                    value: 1m
                )]
            )]
        );

        // Thirty ticks at ten per step turn the stored phase 0 three steps round.
        host.Advance(
            engineTick: 30UL,
            tick: 30UL
        );
        evaluator.Evaluate(
            latch: new RuleLatch(),
            rules: compiled,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "out"
            ),
            expected: 1L
        );
    }
}
