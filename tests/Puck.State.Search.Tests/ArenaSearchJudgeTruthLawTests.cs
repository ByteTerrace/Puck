using Xunit;

namespace Puck.State.Search.Tests;

/// <summary>Proves a search judge evaluating rules over a cell carrying a <see cref="StateDynamics"/> trait reads the
/// stored truth, not the follower's eased sample.</summary>
public sealed class ArenaSearchJudgeTruthLawTests {
    private static readonly DynamicsRow[] Dynamics = [
        new(
            Damping: 1f,
            Frequency: 1f,
            Name: "ease",
            Response: 0f
        )
    ];

    private static StateSection Section() => new(Rows: [
        new StateRow(
            Name: SearchFixture.Name(value: "eased"),
            Kind: CellKind.Int,
            Dynamics: new StateDynamics(Row: "ease"),
            Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
        ),
        new StateRow(
            Name: SearchFixture.Name(value: "verdict"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
        )
    ]);

    [Fact]
    public void SearchJudgeOverAnEasedCellReadsStoredTruth() {
        var section = Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var host = new ArenaSearchEffectHost(
            arena: arena,
            dynamics: Dynamics,
            ticksPerSecond: 30
        );

        var easedOrdinal = catalog.Descriptors.Single(predicate: d => (d.Name == "eased")).Ordinal;
        var slotKey = catalog.Keys.Intern(name: StateRow.SlotKey);

        // Retarget eased cell to 100 at tick 0.
        Assert.True(condition: arena.TryWriteLive(
            key: slotKey,
            operand: 100L,
            reason: out var reason,
            rowOrdinal: easedOrdinal,
            time: host.Time,
            write: StateWriteKind.Set
        ), userMessage: reason);

        // Judge rule: when eased >= 100, verdict = 1.
        var context = new Puck.State.Rules.RuleCompileContext(
            catalog: catalog,
            generators: null,
            patterns: null,
            section: section,
            simulationRateHz: 30,
            tables: null,
            vocabulary: Puck.State.Rules.RuleVocabulary.Core
        );
        var rule = new Rule(
            Effects: [new ActionEffect.SetState(
                State: "verdict",
                Value: 1m
            )],
            Gate: new ActionPredicate.CompareState(
                Comparison: ExpressionOp.GreaterOrEqual,
                State: "eased",
                Value: 100m
            ),
            Mode: ActionTriggerMode.Level,
            Name: SearchFixture.Name(value: "judgeRule")
        );
        var compiled = Puck.State.Rules.RuleCompiler.CompileAll(
            context: context,
            rules: [rule]
        );

        var judge = new RuleArenaSearchJudge(
            host: host,
            rules: compiled
        );

        // Advance to tick 1 (mid-ease). A truth read sees 100 and accepts the candidate.
        var view = new ArenaSearchView(
            Arena: arena,
            EngineTick: 1UL,
            Ply: 0,
            Tick: 1UL
        );

        var accepted = judge.Judge(view: in view);

        Assert.True(
            condition: accepted,
            userMessage: "Search judge read the eased follower sample instead of stored truth"
        );
    }
}
