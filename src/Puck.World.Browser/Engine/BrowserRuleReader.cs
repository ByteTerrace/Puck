using Puck.Maths;
using ArenaEffectHost = Puck.State.Rules.ArenaEffectHost;
using RuleEvaluation = Puck.State.Rules.RuleEvaluation;
using RuleEvaluator = Puck.State.Rules.RuleEvaluator;
using RuleLatch = Puck.State.Rules.RuleLatch;

namespace Puck.World.Browser.Engine;

/// <summary>One hostless answer a world operand received from <see cref="BrowserRuleReader"/> during a judged tick —
/// the shape <c>Judge</c> exports as <c>hostFacts[]</c>, so an author can see which rules read a fact this build has
/// no host to honestly answer from real bodies, machines, a clock, or adjacencies.</summary>
/// <param name="Rule">The rule whose evaluation read the operand.</param>
/// <param name="Operand">The operand's own type name (e.g. <c>"PhysicsQuiescentOperand"</c>).</param>
/// <param name="Answer">The hostless value returned, formatted the same way a trace's own bindings already are (see
/// <see cref="RuleEvaluation.DescribeFact(in RuleFact)"/>) — <c>absent</c>, <c>forever</c>, or the raw value.</param>
public readonly record struct BrowserHostFact(string Rule, string Operand, string Answer);
/// <summary>The session's own effect host: a <see cref="ArenaEffectHost"/> over the session's
/// <see cref="StateArena"/>, widened to <see cref="IWorldFacts"/> — the world's seventeen operand facts, the two
/// body-reference resolutions, and the host-owned row reads <c>Puck.World.Server.WorldServer</c> answers from real
/// bodies, machines, a clock, and adjacencies. This engine ships none of those, so each read answers the vacuous
/// fact a world with no bodies, no machines, no clock, and no adjacencies gives, and each host-owned row read
/// answers "this host serves none". Every such read is recorded into <see cref="HostFacts"/>, so a judged tick's
/// trace shows an author which rules leaned on a fact this engine cannot supply.</summary>
/// <remarks>
/// <para>The evaluator hands every operand's own read the host it was constructed over, so the object judging a
/// tick must itself be the <see cref="IWorldFacts"/> the world's facts cast to. <see cref="RuleNeeds.Admit"/>
/// refuses a rule whose facet this host does not advertise before any of its reads run, and the session asks that
/// question once per document load.</para>
/// <para>An effect arm that lands outside the arena (a pose, a cue, a HUD or placement upsert) reaches
/// <see cref="Fire"/>, which does nothing outward and reports success, so the firing's arena writes still
/// commit.</para>
/// </remarks>
public sealed class BrowserRuleReader : ArenaEffectHost, IWorldFacts {
    /// <summary>The most hostless reads one judged tick captures — a forEach-heavy document could otherwise grow
    /// this list unboundedly in one call, the same ceiling <see cref="BrowserJudgeLimits.MaxTraceEvaluations"/>
    /// already applies to the rule trace.</summary>
    public const int MaxHostFacts = 4096;

    private readonly List<BrowserHostFact> m_hostFacts = [];

    /// <summary>Initializes the host over one arena.</summary>
    /// <param name="arena">The store every read and write addresses.</param>
    /// <param name="generators">The document's declared draw sources.</param>
    /// <param name="ticksPerSecond">The document's simulation rate.</param>
    /// <param name="dynamics">The document's declared dynamics rows.</param>
    /// <param name="instanceIdentity">The identity every draw site's seed folds.</param>
    public BrowserRuleReader(StateArena arena, IReadOnlyList<GeneratorRow>? generators = null, int ticksPerSecond = 0, IReadOnlyList<DynamicsRow>? dynamics = null, string instanceIdentity = "") : base(
        arena: arena,
        dynamics: dynamics,
        generators: generators,
        instanceIdentity: instanceIdentity,
        sites: ((arena is null) ? null : WorldDrawSites.Of(catalog: arena.Catalog)),
        ticksPerSecond: ticksPerSecond
    ) {
        Evaluator = new RuleEvaluator(host: this);
    }

    /// <summary>Gets the evaluator a judged tick runs through.</summary>
    public RuleEvaluator Evaluator { get; }
    /// <summary>Gets every hostless world-operand read since the last <see cref="BeginJudge"/>, in read order.</summary>
    public IReadOnlyList<BrowserHostFact> HostFacts => m_hostFacts;

    /// <summary>Gets the edge latch <see cref="BeginJudge"/> clears before each judged tick.</summary>
    public RuleLatch Latch { get; } = new();
    /// <summary>Gets or sets the rule a recorded host fact is attributed to — the session sets it before it
    /// evaluates each rule, since the evaluator carries no rule identity a host can read.</summary>
    public string RuleName { get; set; } = string.Empty;

    // An arm that lands outside the arena has no host here to land on. It does nothing outward and reports success,
    // so a firing whose effects wrote the arena still commits rather than rewinding on an arm this build cannot run.
    /// <inheritdoc/>
    public override bool Fire(ICompiledFact effect, in EffectFiring firing, out EffectRefusal refusal) {
        refusal = EffectRefusal.None;

        return true;
    }

    // IWorldFacts — the seventeen world-operand facts, each the vacuous answer this build's own documentation
    // commits to for "no such body"/"no such machine"/"no such clock"/"no such link", applied here for "there is no
    // host at all": population 0, physics vacuously quiescent, no clock, no region occupants, no machine byte, no
    // argmax/argmin/nearest body (-1), the engine's largest distance between two bodies that do not exist, no sight
    // line, never parked, perfectly upright, a link never established, a zero channel, and no navigation state.
    // PlacementInfluenceOperand alone reads Absent — an unrepresented influence provider is unknowable, never a
    // falsely safe zero, exactly as WorldServer.Influence.cs already answers it for the one real case that reads
    // Absent today.
    RuleFact IWorldFacts.Read(PopulationOperand operand) => Record(
        operand: nameof(PopulationOperand),
        fact: RuleFact.Finite(
            kind: CellKind.Int,
            value: 0L
        )
    );
    RuleFact IWorldFacts.Read(PhysicsQuiescentOperand operand) => Record(
        operand: nameof(PhysicsQuiescentOperand),
        fact: RuleFact.Finite(
            kind: CellKind.Bool,
            value: 1L
        )
    );
    RuleFact IWorldFacts.Read(ClockOperand operand) => Record(
        operand: nameof(ClockOperand),
        fact: RuleFact.Finite(
            kind: CellKind.Int,
            value: 0L
        )
    );
    RuleFact IWorldFacts.Read(RegionOccupancyOperand operand) => Record(
        operand: nameof(RegionOccupancyOperand),
        fact: RuleFact.Finite(
            kind: CellKind.Int,
            value: 0L
        )
    );
    RuleFact IWorldFacts.Read(PlacementInfluenceOperand operand) => Record(
        operand: nameof(PlacementInfluenceOperand),
        fact: RuleFact.Absent(kind: CellKind.Int)
    );
    RuleFact IWorldFacts.Read(MachineMemoryOperand operand) => Record(
        operand: nameof(MachineMemoryOperand),
        fact: RuleFact.Finite(
            kind: CellKind.Int,
            value: 0L
        )
    );
    RuleFact IWorldFacts.Read(ArgBodyOperand operand) => Record(
        operand: nameof(ArgBodyOperand),
        fact: RuleFact.Finite(
            kind: CellKind.Int,
            value: -1L
        )
    );
    RuleFact IWorldFacts.Read(BodyDistanceOperand operand) => Record(
        operand: nameof(BodyDistanceOperand),
        fact: RuleFact.Finite(
            value: FixedQ4816.MaxValue.Value,
            kind: CellKind.Fixed
        )
    );
    RuleFact IWorldFacts.Read(LineOfSightOperand operand) => Record(
        operand: nameof(LineOfSightOperand),
        fact: RuleFact.Finite(
            kind: CellKind.Bool,
            value: 0L
        )
    );
    RuleFact IWorldFacts.Read(ParkedOperand operand) => Record(
        operand: nameof(ParkedOperand),
        fact: RuleFact.Finite(
            kind: CellKind.Int,
            value: 0L
        )
    );
    RuleFact IWorldFacts.Read(UprightOperand operand) => Record(
        operand: nameof(UprightOperand),
        fact: RuleFact.Finite(
            value: FixedQ4816.One.Value,
            kind: CellKind.Fixed
        )
    );
    RuleFact IWorldFacts.Read(BodyFactOperand operand) => Record(
        operand: nameof(BodyFactOperand),
        fact: RuleFact.Finite(
            kind: CellKind.Int,
            value: 0L
        )
    );
    RuleFact IWorldFacts.Read(LinkStalenessOperand operand) => Record(
        operand: nameof(LinkStalenessOperand),
        fact: RuleFact.Finite(
            kind: CellKind.Int,
            value: 0L
        )
    );
    RuleFact IWorldFacts.Read(ChannelOperand operand) => Record(
        operand: nameof(ChannelOperand),
        fact: RuleFact.Finite(
            value: FixedQ4816.Zero.Value,
            kind: CellKind.Fixed
        )
    );
    RuleFact IWorldFacts.Read(PointerOperand operand) => Record(
        operand: nameof(PointerOperand),
        fact: RuleFact.Finite(
            value: 0L,
            kind: operand.ValueKind
        )
    );
    RuleFact IWorldFacts.Read(NearestOperand operand) => Record(
        operand: nameof(NearestOperand),
        fact: RuleFact.Finite(
            kind: CellKind.Int,
            value: -1L
        )
    );
    RuleFact IWorldFacts.Read(NavigationOperand operand) => Record(
        operand: nameof(NavigationOperand),
        fact: RuleFact.Finite(
            kind: CellKind.Int,
            value: 0L
        )
    );
    RuleFact IWorldFacts.Read(BoardCellOfOperand operand) => Record(
        operand: nameof(BoardCellOfOperand),
        fact: RuleFact.Finite(
            kind: CellKind.Int,
            value: -1L
        )
    );
    // No body reference ever resolves, and a $pair: key over two unresolved bodies names no cell — the invalid
    // default key, which reads absent and addresses no write.
    int IWorldFacts.ResolveBody(in CompiledBodyRef bodyRef) => -1;
    CellKey IWorldFacts.PairKey(PairKeyFact key) => default;
    // This build serves no host-owned row: the physics field lattice a world's own storage would answer from is one
    // of the things it does not ship.
    bool IWorldFacts.TryReadHostOwnedCell(int rowOrdinal, int cell, out long value) {
        value = 0L;

        return false;
    }
    bool IWorldFacts.TryReadHostOwnedSlot(int rowOrdinal, out CellValue value) {
        value = default;

        return false;
    }

    private RuleFact Record(string operand, RuleFact fact) {
        if (m_hostFacts.Count < MaxHostFacts) {
            m_hostFacts.Add(item: new BrowserHostFact(
                Answer: RuleEvaluation.DescribeFact(fact: in fact),
                Operand: operand,
                Rule: RuleName
            ));
        }

        return fact;
    }

    /// <summary>Clears the hostless-read ledger and the edge latch, so a judged tick's verdict is a function of the
    /// position it reads rather than of the tick judged before it.</summary>
    public void BeginJudge() {
        m_hostFacts.Clear();
        Latch.Clear();
    }
}
