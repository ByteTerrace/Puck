using Xunit;

namespace Puck.State.Tests;

/// <summary>The search runtime is state-engine vocabulary: it runs over a bare row list and a hand-built plan, with
/// no world, document, or wire concept anywhere in reach.</summary>
public sealed class SearchRuntimeLawTests {
    private sealed class Section(IReadOnlyList<StateRow> rows) : IStateSection {
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public IReadOnlyList<StateRow> Rows => rows;
    }

    private static StateRow Keyed(string name, params (string Key, long Value)[] cells) =>
        new(
            Name: Name(value: name),
            Kind: CellKind.Int,
            Capacity: 8,
            Cells: [.. cells.Select(selector: static c => new StateCell(
                    Key: Name(value: c.Key),
                    Value: c.Value
                ))]
        );
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateRow Slot(string name, long value) =>
        new(
            Name: Name(value: name),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: value
                )]
        );

    // A finished job's outputs refused by the mutation door narrate once, through the caller's own delegate — no
    // WorldOutputHub, WorldMutation, or Server type anywhere in the runtime's own dependency closure.
    [Fact]
    public void ARefusedLandingNarratesThroughTheCallersOwnDelegate() {
        StateRow[] rows = [
            Keyed(
                name: "piece",
                ("a", 0L),
                ("b", 1L)
            ),
            Slot(
                name: "turn",
                value: 0L
            ),
            Slot(
                name: "verdict",
                value: 0L
            ),
            Keyed(
                name: "legal",
                ("a", 0L),
                ("b", 0L)
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
        var narrated = new List<(string Channel, string Text)>();
        var runtime = new SearchRuntime(
            live: () => rows,
            narrate: (channel, text) => narrated.Add(item: (channel, text))
        );

        runtime.Rebuild(
            plans: [plan],
            judge: judge,
            rows: rows,
            catalog: catalog,
            topology: static _ => null,
            patterns: CompiledPatterns.Empty,
            tables: []
        );

        var installed = runtime.Step(
            engineTick: 0UL,
            apply: static _ => false,
            tick: 1UL
        );

        Assert.False(condition: installed);
        Assert.Single(collection: narrated);
        Assert.Equal(
            "world.search",
            narrated[0].Channel
        );
        Assert.Contains(
            "moves",
            narrated[0].Text
        );
    }
    // Two tokens on a four-cell abstract board (Relocate needs no topology at all — only Jump and Pair ever
    // dereference SearchPlan.Topology), a rule that unconditionally accepts every candidate by flipping turn, and no
    // score: the root walk exhausts every (token, target) pair in one tick and lands a legal mask per token.
    [Fact]
    public void ARelocateJobLandsEveryTokensLegalMaskAsSearchWrites() {
        StateRow[] rows = [
            Keyed(
                name: "piece",
                ("a", 0L),
                ("b", 1L)
            ),
            Slot(
                name: "turn",
                value: 0L
            ),
            Slot(
                name: "verdict",
                value: 0L
            ),
            Keyed(
                name: "legal",
                ("a", 0L),
                ("b", 0L)
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
            engineTick: 0UL,
            tick: 1UL,
            apply: writes => { landed = writes; return true; }
        );

        Assert.True(condition: installed);
        Assert.NotNull(@object: landed);
        Assert.Equal(
            2,
            landed!.Count
        );
        Assert.Contains(
            collection: landed,
            filter: write => (write is SearchWrite.Cell { Row: "legal", Key: "a", Value: 0b1110L })
        );
        Assert.Contains(
            collection: landed,
            filter: write => (write is SearchWrite.Cell { Row: "legal", Key: "b", Value: 0b1101L })
        );
    }
}
