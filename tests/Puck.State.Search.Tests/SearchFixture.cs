using Xunit;

namespace Puck.State.Search.Tests;

/// <summary>Judges a candidate's position through a caller's own delegate.</summary>
internal delegate bool JudgeStep(in ArenaSearchView view);
/// <summary>Scores the position the arena holds through a caller's own delegate.</summary>
internal delegate long ScoreStep(in ArenaSearchView view);

/// <summary>A judge of two delegates, for a law that wants to say exactly what a candidate's verdict is without
/// authoring rules for it.</summary>
internal sealed class DelegateJudge(StateArena arena, JudgeStep judge, ScoreStep? score = null, IReadOnlyList<int>? keyRows = null) : IArenaSearchJudge {
    public StateArena Arena => arena;
    public IReadOnlyList<int> KeyRows => (keyRows ?? []);
    public bool Scores => (score is not null);

    public bool Judge(in ArenaSearchView view) => judge(view: in view);
    public long Score(in ArenaSearchView view) => (score?.Invoke(view: in view) ?? 0L);
    public bool TryAdmit(ArenaSearchPlan plan, out string refusal) {
        refusal = string.Empty;

        return true;
    }
}
/// <summary>One position authored once: an arena seeded from the section, and a compile context over that same
/// section, so an authored rule compiles against the catalog the arena stores.</summary>
internal sealed class Position {
    public Position(StateRow[] rows, bool reverseInternOrder = false, IReadOnlyList<StateRecord>? records = null, IReadOnlyList<StatePool>? pools = null) {
        var section = new Section(pools: pools, records: records, rows: rows);
        var catalog = StateCatalog.Compile(section: (reverseInternOrder
            ? new Section(rows: [.. rows.Select(selector: static row => row with {
                Cells = ((row.Cells is null)
                    ? null
                    : [.. row.Cells.Reverse()]
                ),
            })], records: records, pools: pools)
            : section
        ));

        Arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        Catalog = Arena.Catalog;
        Rows = rows;
        RulesContext = new Puck.State.Rules.RuleCompileContext(
            catalog: Catalog,
            generators: null,
            patterns: null,
            section: section,
            simulationRateHz: 240,
            tables: null,
            vocabulary: Puck.State.Rules.RuleVocabulary.Core
        );
        SlotKey = Catalog.Keys.Intern(name: StateRow.SlotKey);
    }

    public StateArena Arena { get; }
    public StateCatalog Catalog { get; }
    public StateRow[] Rows { get; }
    public Puck.State.Rules.RuleCompileContext RulesContext { get; }
    public CellKey SlotKey { get; }

    public CellKey Key(string name) => Catalog.Keys.Intern(name: SearchFixture.Name(value: name));
    public int Ordinal(string name) {
        Assert.True(
            condition: Catalog.TryResolve(
                handle: out var handle,
                lane: StateLane.Document,
                name: name
            ),
            userMessage: name
        );

        return handle.Ordinal;
    }

    private sealed class Section(IReadOnlyList<StateRow> rows, IReadOnlyList<StateRecord>? records, IReadOnlyList<StatePool>? pools) : IStateSection {
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public IReadOnlyList<StatePool>? Pools => pools;
        public IReadOnlyList<StateRecord>? Records => records;
        public IReadOnlyList<StateRow> Rows => rows;
    }
}
/// <summary>The rows, plans, compilers, and runners every search law is written on.</summary>
internal static class SearchFixture {
    public static StateRow[] Board(int tokens, int cells) {
        var pieces = new (string Key, long Value)[tokens];
        var outputs = new (string Key, long Value)[tokens];

        for (var index = 0; (index < tokens); index++) {
            outputs[index] = ($"t{index}", 0L);
            pieces[index] = ($"t{index}", (index % cells));
        }

        return [
            Keyed(
                name: "piece",
                cells: pieces
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
                cells: outputs
            ),
            Keyed(
                name: "counts",
                cells: outputs
            ),
            Keyed(
                name: "best",
                ("token", -1L),
                ("to", -1L),
                ("score", 0L)
            ),
        ];
    }
    public static StateRow Keyed(string name, params (string Key, long Value)[] cells) =>
        new(
            Name: Name(value: name),
            Kind: CellKind.Int,
            Capacity: 16,
            Cells: [.. cells.Select(selector: static cell => new StateCell(
                    Key: Name(value: cell.Key),
                    Value: CellValue.Int(value: cell.Value)
                ))]
        );
    public static CellName Name(string value) => CellName.Parse(candidate: value);
    public static StateRow Slot(string name, long value) =>
        new(
            Name: Name(value: name),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: CellValue.Int(value: value)
                )]
        );
    // The one rule a judge reads: accept every resolved candidate and hand the turn to the other side.
    public static Rule AcceptEveryCandidate() =>
        new(
            Name: Name(value: "accept"),
            Effects: [
                new ActionEffect.SetState(
                    State: "verdict",
                    Value: 1m
                ),
                new ActionEffect.SetState(
                    Expression: ExpressionProgram.Parse(text: "turn + 1"),
                    State: "turn"
                ),
            ]
        );
    public static Puck.State.Rules.CompiledExpressionToken[] CompileRules(string text, Puck.State.Rules.RuleCompileContext context) => Puck.State.Rules.RuleCompiler.CompileExpression(
        context: context,
        expression: Parse(text: text),
        kind: CellKind.Int,
        ruleName: "score",
        verb: "search score"
    );
    public static Puck.State.Rules.CompiledRule[] CompileRulesJudge(Position position, params Rule[] rules) => [
        .. rules.Select(selector: rule => Puck.State.Rules.RuleCompiler.Compile(
        context: position.RulesContext,
        rule: rule
    )),
    ];
    public static ExpressionProgram Parse(string text) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                program: out var parsed,
                text: text
            ),
            userMessage: error
        );

        return parsed!;
    }
    // The judge the arena walk runs: the authored rules, compiled for the new compiler and evaluated over the
    // scoped arena through a host whose arms never reach outside it.
    public static RuleArenaSearchJudge RuleJudge(Position position, Rule[] rules, string? score = null) => new(
        host: new ArenaSearchEffectHost(arena: position.Arena),
        rules: CompileRulesJudge(
            position: position,
            rules: rules
        ),
        score: ((score is null)
            ? null
            : CompileRules(
                context: position.RulesContext,
                text: score
            ))
    );
    public static ArenaSearch Build(Position position, SearchPlan plan, IArenaSearchJudge judge, ulong drawSeed = 0UL) {
        Assert.True(
            condition: ArenaSearchPlan.TryResolve(
                catalog: position.Catalog,
                drawSeed: drawSeed,
                plan: plan,
                reason: out var reason,
                resolved: out var resolved
            ),
            userMessage: reason
        );

        var search = new ArenaSearch(arena: position.Arena);

        Assert.True(
            condition: search.Rebuild(
                judges: [judge],
                plans: [resolved],
                reason: out reason
            ),
            userMessage: reason
        );

        return search;
    }
    // One authored position, one authored plan and one authored rule set run through the arena runtime, landed as
    // named writes. A scored job reads its score through its judge, never off the plan.
    public static List<(string Row, string Key, long Value)> RunRules(
        StateRow[] rows, Func<Position, SearchPlan> makePlan, Rule[] rules, string? score = null, ulong drawSeed = 0UL,
        ulong tick = 1UL, ulong engineTick = 0UL
    ) {
        var arena = new Position(rows: rows);

        return RunArena(
            catalog: arena.Catalog,
            engineTick: engineTick,
            search: Build(
                drawSeed: drawSeed,
                judge: RuleJudge(
                    position: arena,
                    rules: rules,
                    score: score
                ),
                plan: makePlan(arg: arena),
                position: arena
            ),
            tick: tick
        );
    }
    public static List<(string Row, string Key, long Value)> Normalize(IReadOnlyList<ArenaSearchWrite> writes, StateCatalog catalog) {
        var normalized = new List<(string Row, string Key, long Value)>();

        foreach (var write in writes) {
            normalized.Add(item: (write switch {
                ArenaSearchWrite.Cell cell => (catalog.Descriptors[cell.RowOrdinal].Name, catalog.Keys[cell.Key].Value, cell.Value),
                ArenaSearchWrite.ClearBoard clear => (catalog.Descriptors[clear.RowOrdinal].Name, "*clear", 0L),
                _ => ("?", "?", 0L),
            }));
        }

        return normalized;
    }
    public static List<(string Row, string Key, long Value)> RunArena(ArenaSearch search, StateCatalog catalog, ulong tick = 1UL, ulong engineTick = 0UL) {
        IReadOnlyList<ArenaSearchWrite>? landed = null;

        for (var step = 0; ((step < 4_096) && (landed is null)); step++) {
            _ = search.Step(
                apply: writes => {
                    landed = writes;

                    return true;
                },
                engineTick: engineTick,
                tick: tick
            );
        }

        Assert.NotNull(@object: landed);

        return Normalize(
            catalog: catalog,
            writes: landed
        );
    }
}
