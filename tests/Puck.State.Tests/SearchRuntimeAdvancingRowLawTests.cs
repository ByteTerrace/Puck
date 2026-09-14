using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a search job's judge rules read an advancing row's live value at the ENGINE tick
/// <see cref="SearchRuntime.Step"/> is given, never the simulation tick — the same engine-tick coordinate
/// <c>WorldServer.CompletedEngineTicks</c> supplies live. The gauge's accumulation is derived independently here from
/// <see cref="Puck.Maths.FixedTickConversion.TicksPerSecond"/> alone, never from <see cref="StateAdvance"/>.</summary>
public sealed class SearchRuntimeAdvancingRowLawTests {
    private sealed class Section(IReadOnlyList<StateRow> rows) : IStateSection {
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public IReadOnlyList<StateRow> Rows => rows;
    }

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateRow Keyed(string name, params (string Key, long Value)[] cells) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Capacity: 8,
        Cells: [.. cells.Select(selector: static c => new StateCell(
                Key: Name(value: c.Key),
                Value: c.Value
            ))]
    );
    private static StateRow Slot(string name, long value = 0L, StateAdvance? advance = null) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Advance: advance,
        Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: value
            )]
    );

    // A Relocate job's judge rule accepts a candidate only while a live-advancing "gauge" row reads at least 5 —
    // a value that depends entirely on which ENGINE tick the job is stepped at, never the (unrelated, arbitrary)
    // simulation tick argument. Two runs of the identical plan/rows, one with a small tick and a large engine tick
    // and one with the reverse, prove the engine-tick coordinate alone decides.
    private static (bool Installed, IReadOnlyList<SearchWrite>? Writes) RunAcceptGatedByGauge(ulong tick, ulong engineTick) {
        StateRow[] rows = [
            Keyed(
                name: "piece",
                ("a", 0L),
                ("b", 1L)
            ),
            Slot(name: "turn"),
            Slot(name: "verdict"),
            Keyed(
                name: "legal",
                ("a", 0L),
                ("b", 0L)
            ),
            Slot(
                name: "gauge",
                advance: new StateAdvance(
                    PerSecondDenominator: 1,
                    PerSecondNumerator: 1
                )
            ),
        ];
        var section = new Section(rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(
            section: section,
            catalog: catalog,
            tables: null,
            patterns: null,
            generators: null,
            simulationRateHz: 240,
            vocabulary: RuleVocabulary.Core
        );
        var judge = new[] {
            RuleCompiler.Compile(
            rule: new Rule(
                Name: Name(value: "accept"),
                Gate: new ActionPredicate.CompareState(
                    State: "gauge",
                    Comparison: ActionStateComparison.GreaterOrEqual,
                    Value: 5m
                ),
                Effects: [
                    new ActionEffect.SetState(
                        State: "verdict",
                        Value: 1m
                    ),
                    new ActionEffect.SetState(
                        State: "turn",
                        Value: 1m
                    ),
                ]
            ),
            context: context
        ),
        };
        var plan = new SearchPlan(
            Name: "moves",
            Tokens: "piece",
            Topology: null,
            Zones: [],
            CellCount: 4,
            Turn: "turn",
            Verdict: "verdict",
            Off: -1L,
            Nodes: 64,
            JudgeCost: 1L,
            Depth: 1,
            Score: null,
            Best: null,
            Shapes: [new SearchShapePlan(
                    Kind: SearchShapeKind.Relocate,
                    Displace: true,
                    Directions: [],
                    PairWithIndex: -1
                )],
            Legal: "legal"
        );
        var runtime = new SearchRuntime(live: () => rows);

        runtime.Rebuild(
            plans: [plan],
            judge: judge,
            rows: rows,
            catalog: catalog,
            topology: static _ => null,
            patterns: CompiledPatterns.Empty,
            tables: []
        );

        IReadOnlyList<SearchWrite>? landed = null;
        var installed = runtime.Step(
            tick: tick,
            engineTick: engineTick,
            apply: writes => { landed = writes; return true; }
        );

        return (installed, landed);
    }

    [Fact]
    public void AJudgeRuleReadsAnAdvancingRowAtTheSuppliedEngineTickRegardlessOfTheSimulationTick() {
        // 5 seconds of engine time have completed (5 * 50400 engine ticks): gauge reads 5, the gate holds, and
        // every candidate is accepted — even though the simulation tick argument is small and unrelated.
        var (acceptedInstalled, acceptedWrites) = RunAcceptGatedByGauge(
            tick: 1UL,
            engineTick: (5UL * Puck.Maths.FixedTickConversion.TicksPerSecond)
        );

        Assert.True(condition: acceptedInstalled);
        Assert.NotNull(@object: acceptedWrites);
        Assert.Contains(
            collection: acceptedWrites,
            filter: write => (write is SearchWrite.Cell { Row: "legal", Key: "a", Value: 0b1110L })
        );
        Assert.Contains(
            collection: acceptedWrites,
            filter: write => (write is SearchWrite.Cell { Row: "legal", Key: "b", Value: 0b1101L })
        );

        // No engine time has completed (engineTick 0): gauge reads 0, the gate never holds, and nothing is
        // accepted — even though the simulation tick argument is now large. Neither token's full-acceptance mask
        // (the one both candidates land on above) appears among whatever the job did output.
        var (_, rejectedWrites) = RunAcceptGatedByGauge(
            tick: 999_999UL,
            engineTick: 0UL
        );

        Assert.DoesNotContain(
            collection: (rejectedWrites ?? []),
            filter: write => (write is SearchWrite.Cell { Row: "legal", Key: "a", Value: 0b1110L })
        );
        Assert.DoesNotContain(
            collection: (rejectedWrites ?? []),
            filter: write => (write is SearchWrite.Cell { Row: "legal", Key: "b", Value: 0b1101L })
        );
    }
}
