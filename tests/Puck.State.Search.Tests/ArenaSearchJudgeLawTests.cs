using Xunit;

using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: a candidate's judge is the rule evaluator running the job's compiled judge rules
/// over the scoped arena through an effect host. An arm the firing cannot rewind is queued and dropped rather than
/// fired, because the candidate it belongs to is rewound; a plan whose rules need a facet the judge's host does not
/// serve is refused by name when the job set is installed, and never again.</summary>
public sealed class ArenaSearchJudgeLawTests {
    [Fact]
    public void RewindTurnIsRefusedAtSearchAdmission() {
        var position = new Position(rows: Rows());
        var judge = new RuleArenaSearchJudge(
            host: new ArenaSearchEffectHost(arena: position.Arena),
            rules: [new Puck.State.Rules.CompiledRule(
                Name: "undo",
                Mode: ActionTriggerMode.Level,
                Gate: [],
                Effects: [new Puck.State.Rules.RewindTurnEffect(group: "play")],
                Needs: RuleNeeds.None
            )]
        );

        Assert.True(condition: ArenaSearchPlan.TryResolve(catalog: position.Catalog, plan: Plan(), reason: out var reason, resolved: out var resolved), userMessage: reason);
        Assert.False(condition: judge.TryAdmit(plan: resolved, refusal: out var refusal));
        Assert.Contains(actualString: refusal, comparisonType: StringComparison.Ordinal, expectedSubstring: "rewindTurn");
        Assert.Contains(actualString: refusal, comparisonType: StringComparison.Ordinal, expectedSubstring: "settled authoritative turn boundary");
    }

    /// <summary>A capability no search host serves.</summary>
    public interface ICardFacts : IFacet {
        /// <summary>Returns the top card the host is holding.</summary>
        long Top();
    }
    /// <summary>An arm whose firing leaves the arena, and which therefore reaches the host's own door.</summary>
    public sealed record Stamp : ActionEffect;

    // A host that performs what an irreversible arm asks: the outward act stands in for a save, a HUD upsert, or
    // anything else a candidate must not cause.
    private sealed class OutwardHost(StateArena arena, int savedOrdinal, CellKey slot) : Puck.State.Rules.ArenaEffectHost(arena: arena) {
        public int Fired { get; private set; }

        public override bool Fire(ICompiledFact effect, in EffectFiring firing, out EffectRefusal refusal) {
            refusal = EffectRefusal.None;

            if (!firing.Preflight) {
                Fired++;

                _ = Arena.TryWrite(
                    key: slot,
                    operand: 1L,
                    reason: out _,
                    rowOrdinal: savedOrdinal,
                    write: StateWriteKind.Set
                );
            }

            return true;
        }
    }
    private sealed class StampArm : Puck.State.Rules.EffectFamily {
        public override string Discriminator => "stamp";
        public override Type EffectType => typeof(Stamp);

        public override Puck.State.Rules.IRuleEffect Compile(ActionEffect effect, string ruleName, Puck.State.Rules.RuleCompileContext context) => new StampEffect();
    }
    private sealed class StampEffect() : Puck.State.Rules.RuleEffect(describe: "stamp") {
        public override EffectNeeds Needs => EffectNeeds.Irreversible;

        public override RuleWork Cost(IRuleCostContext context) => 1L;
    }
    // Counts how often the search asks whether it may run at all.
    private sealed class CountingJudge(IArenaSearchJudge inner) : IArenaSearchJudge {
        public int Admissions { get; private set; }
        public StateArena Arena => inner.Arena;
        public IReadOnlyList<int> KeyRows => inner.KeyRows;
        public bool ReadsTick => inner.ReadsTick;
        public bool Scores => inner.Scores;

        public bool Judge(in ArenaSearchView view) => inner.Judge(view: in view);
        public long Score(in ArenaSearchView view) => inner.Score(view: in view);
        public bool TryAdmit(ArenaSearchPlan plan, out string refusal) {
            Admissions++;

            return inner.TryAdmit(
                plan: plan,
                refusal: out refusal
            );
        }
    }

    private static StateRow[] Rows() => [
        .. Board(
            cells: 4,
            tokens: 2
        ),
        Slot(
            name: "saved",
            value: 0L
        ),
    ];
    private static Rule AcceptAndStamp() =>
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
                new Stamp(),
            ]
        );
    private static SearchPlan Plan() =>
        new(
            Name: "moves",
            Tokens: "piece",
            Topology: null,
            Zones: [],
            CellCount: 4,
            Turn: "turn",
            Verdict: "verdict",
            Off: -1L,
            Nodes: 256,
            Work: SearchWork.NodeBounded(judge: 1L),
            Depth: 1,
            Best: "best",
            Shapes: [new SearchShapePlan(
                    Kind: SearchShapeKind.Relocate,
                    Displace: false,
                    Directions: [],
                    PairWithIndex: -1
                )],
            Counts: "counts",
            Legal: "legal"
        );
    private static Puck.State.Rules.CompiledRule[] StampingRules(Position position) {
        var section = new StampSection(rows: position.Rows);
        var context = new Puck.State.Rules.RuleCompileContext(
            catalog: position.Catalog,
            generators: null,
            patterns: null,
            section: section,
            simulationRateHz: 240,
            tables: null,
            vocabulary: new Puck.State.Rules.RuleVocabulary(
                effects: [new StampArm()],
                keys: [],
                operands: [],
                predicates: []
            )
        );

        return Puck.State.Rules.RuleCompiler.CompileAll(
            context: context,
            rules: [AcceptAndStamp()]
        );
    }

    [Fact]
    public void AnIrreversibleArmIsQueuedAndDiscardedRatherThanFiredFromACandidate() {
        var control = new Position(rows: Rows());
        var searched = new Position(rows: Rows());
        var saved = control.Ordinal(name: "saved");
        var outward = new OutwardHost(
            arena: control.Arena,
            savedOrdinal: saved,
            slot: control.SlotKey
        );

        // The rules do carry an arm that acts outward: evaluated through a host that performs it, it performs.
        Assert.True(condition: new Puck.State.Rules.RuleEvaluator(host: outward).Evaluate(
            latch: new Puck.State.Rules.RuleLatch(),
            rules: StampingRules(position: control),
            stepTicks: 1UL
        ));
        Assert.Equal(
            actual: outward.Fired,
            expected: 1
        );
        Assert.True(condition: control.Arena.TryRead(
            key: control.SlotKey,
            rowOrdinal: saved,
            value: out var stamped
        ));
        Assert.Equal(
            actual: stamped.AsInt,
            expected: 1L
        );

        var host = new ArenaSearchEffectHost(arena: searched.Arena);
        var search = SearchFixture.Build(
            judge: new RuleArenaSearchJudge(
                host: host,
                rules: StampingRules(position: searched)
            ),
            plan: Plan(),
            position: searched
        );
        var landed = SearchFixture.RunArena(
            catalog: searched.Catalog,
            search: search
        );

        // Every candidate queued the arm and the rewind dropped it, so nothing outside the arena happened and the
        // row the arm writes through its host is untouched.
        Assert.NotEmpty(collection: landed);
        Assert.True(condition: (host.DiscardedArms > 0L));
        Assert.True(condition: searched.Arena.TryRead(
            key: searched.SlotKey,
            rowOrdinal: searched.Ordinal(name: "saved"),
            value: out var unstamped
        ));
        Assert.Equal(
            actual: unstamped.AsInt,
            expected: 0L
        );
    }
    [Fact]
    public void APlanWhoseJudgeNeedsAFacetTheHostDoesNotServeIsRefusedByName() {
        var position = new Position(rows: Rows());
        var needs = new RuleNeedsBuilder();

        needs.Add(facet: FacetRef.Of<ICardFacts>());

        var rules = CompileRulesJudge(
            position: position,
            rules: SearchFixture.AcceptEveryCandidate()
        );
        var judge = new RuleArenaSearchJudge(
            host: new ArenaSearchEffectHost(arena: position.Arena),
            rules: [(rules[0] with { Needs = needs.Build() })]
        );

        Assert.True(
            condition: ArenaSearchPlan.TryResolve(
                catalog: position.Catalog,
                plan: Plan(),
                reason: out var reason,
                resolved: out var resolved
            ),
            userMessage: reason
        );

        var search = new ArenaSearch(arena: position.Arena);

        Assert.False(condition: search.Rebuild(
            judges: [judge],
            plans: [resolved],
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: nameof(ICardFacts)
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "moves"
        );
        Assert.Equal(
            actual: search.Count,
            expected: 0
        );
    }
    [Fact]
    public void AdmissionIsAskedWhenTheJobSetIsInstalledAndNeverPerCandidate() {
        var position = new Position(rows: Rows());
        var judge = new CountingJudge(inner: RuleJudge(
            position: position,
            rules: [SearchFixture.AcceptEveryCandidate()]
        ));
        var search = SearchFixture.Build(
            judge: judge,
            plan: Plan(),
            position: position
        );

        Assert.Equal(
            actual: judge.Admissions,
            expected: 1
        );

        var landed = SearchFixture.RunArena(
            catalog: position.Catalog,
            search: search
        );

        Assert.NotEmpty(collection: landed);
        Assert.Equal(
            actual: judge.Admissions,
            expected: 1
        );
    }
    [Fact]
    public void AJudgeOverAnotherArenaIsRefusedByName() {
        var position = new Position(rows: Rows());
        var other = new Position(rows: Rows());

        Assert.True(
            condition: ArenaSearchPlan.TryResolve(
                catalog: position.Catalog,
                plan: Plan(),
                reason: out var reason,
                resolved: out var resolved
            ),
            userMessage: reason
        );

        var search = new ArenaSearch(arena: position.Arena);

        Assert.False(condition: search.Rebuild(
            judges: [RuleJudge(
                position: other,
                rules: [SearchFixture.AcceptEveryCandidate()]
            )],
            plans: [resolved],
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "another arena"
        );
    }

    private sealed class StampSection(IReadOnlyList<StateRow> rows) : IStateSection {
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public IReadOnlyList<StateRow> Rows => rows;
    }
}
