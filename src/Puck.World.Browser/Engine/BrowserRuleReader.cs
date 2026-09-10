using Puck.Maths;

namespace Puck.World.Browser.Engine;

/// <summary>One hostless answer a world operand received from <see cref="BrowserRuleReader"/> during a judged tick —
/// the shape <c>Judge</c> exports as <c>hostFacts[]</c>, so an author can see which rules read a fact this build has
/// no host to honestly answer from real bodies, machines, a clock, or adjacencies.</summary>
/// <param name="Rule">The rule whose evaluation read the operand.</param>
/// <param name="Operand">The operand's own type name (e.g. <c>"PhysicsQuiescentOperand"</c>).</param>
/// <param name="Answer">The hostless value returned, formatted the same way a trace's own bindings already are (see
/// <see cref="RuleEvaluation.DescribeFact"/>) — <c>absent</c>, <c>forever</c>, or the raw value.</param>
public readonly record struct BrowserHostFact(string Rule, string Operand, string Answer);

/// <summary>The session's own rule reader: wraps a <see cref="FrameHost"/> for every state read and write, and
/// widens it to <see cref="IWorldRuleReader"/> — the world's sixteen operand facts plus the two body-reference
/// resolutions <c>Puck.World.Server.WorldServer</c> answers from real bodies, machines, a clock, and adjacencies —
/// none of which this engine ships (see the project README's "Verified scope boundary"). Each read answers the
/// honest vacuous fact a world with no bodies, no machines, no clock, and no adjacencies gives: the same convention
/// each <c>WorldRuleFacts</c> prefix's own remarks and <c>WorldServer.RuleHost.cs</c>'s "no such body"/"no such
/// machine" reads already commit to for an absent host, applied here for "there is no host at all" rather than
/// invented fresh. A fact no such convention covers (<see cref="PlacementInfluenceOperand"/>'s unrepresented
/// provider) reads <see cref="RuleFact.Absent(CellKind)"/> — the library's own existing "unknowable, not zero"
/// representation, exactly as <c>WorldServer.Influence.cs</c> already uses it. Every hostless read is recorded into
/// <see cref="HostFacts"/> so a judged tick's own trace can show an author which rules leaned on a fact this engine
/// cannot supply, without throwing the <see cref="InvalidCastException"/> a bare <see cref="FrameHost"/> draws the
/// moment such a rule evaluates.</summary>
/// <remarks><see cref="RuleEvaluator.Read(Puck.State.OperandFact, ulong)"/> hands every operand's own
/// <c>OperandFact.Read</c> call the SAME object the evaluator was constructed over as the reader — so the object
/// judging a tick must itself cast to <see cref="IWorldRuleReader"/>, which a bare <see cref="FrameHost"/> never
/// does. This type owns its own <see cref="RuleEvaluator"/>/<see cref="RuleLatch"/> rather than reusing the wrapped
/// <see cref="FrameHost"/>'s own (which stays dormant — <see cref="FrameHost.Judge"/> is never called on it); state
/// reads, writes, and preflight scoping still ride the wrapped host's own frame door unchanged.</remarks>
public sealed class BrowserRuleReader : IRuleHost, IWorldRuleReader {
    /// <summary>The most hostless reads one judged tick captures — a forEach-heavy document could otherwise grow
    /// this list unboundedly in one call, the same ceiling <see cref="BrowserJudgeLimits.MaxTraceEvaluations"/>
    /// already applies to the rule trace.</summary>
    public const int MaxHostFacts = 4096;

    private readonly FrameHost m_frameHost;
    private readonly List<BrowserHostFact> m_hostFacts = [];
    private string? m_boundTokenKey;
    private string? m_boundPreviousKey;
    private bool m_tableKeyMissing;

    /// <summary>Wraps a fresh frame host: state reads, writes, and preflight scoping ride it unchanged.</summary>
    /// <param name="frameHost">The frame host.</param>
    public BrowserRuleReader(FrameHost frameHost) {
        ArgumentNullException.ThrowIfNull(argument: frameHost);

        m_frameHost = frameHost;
        Evaluator = new RuleEvaluator(host: this);
    }

    /// <summary>Gets the wrapped frame every state read and write goes to.</summary>
    public StateFrame Frame => m_frameHost.Frame;
    /// <summary>Gets the evaluator a judged tick runs through — bound to this reader, never the wrapped frame
    /// host's own dormant evaluator.</summary>
    public RuleEvaluator Evaluator { get; }
    /// <summary>Gets the latch <see cref="Judge"/> clears before each run.</summary>
    public RuleLatch Latch { get; } = new();
    /// <summary>Gets every hostless world-operand read the most recent <see cref="Judge"/> call captured, in read
    /// order.</summary>
    public IReadOnlyList<BrowserHostFact> HostFacts => m_hostFacts;

    /// <summary>Rebinds the wrapped frame to rows its layout fits.</summary>
    /// <param name="rows">The rows.</param>
    public void Rebind(IReadOnlyList<StateRow> rows) => m_frameHost.Rebind(rows: rows);
    /// <summary>Judges one tick over the given rules in array order, capturing every hostless world-operand read
    /// this tick made.</summary>
    /// <param name="rules">The rules, already restricted to what a frame can evaluate.</param>
    /// <param name="tick">The tick the reads answer as of.</param>
    /// <returns><see langword="true"/> when any effect wrote the frame.</returns>
    public bool Judge(CompiledRule[] rules, ulong tick) {
        m_hostFacts.Clear();
        Latch.Clear();

        return Evaluator.Evaluate(rules: rules, latch: Latch, tick: tick, stepTicks: 1UL);
    }

    // IRuleReader — the evaluation in flight (Tick, BoundEachKey, BoundIndex, BindingValue, table-key reporting) is
    // OUR OWN Evaluator's, never the wrapped frame host's dormant one; everything else about the section being read
    // (the store, the catalog, the patterns, board scratch, row versions) rides the wrapped host unchanged.
    ulong IRuleReader.Tick => Evaluator.Tick;
    StateStore IRuleReader.Store => m_frameHost.Store;
    StateCatalog IRuleReader.Catalog => m_frameHost.Catalog;
    CompiledPatterns IRuleReader.Patterns => m_frameHost.Patterns;
    string? IRuleReader.BoundEachKey => Evaluator.BoundEachKey;
    string? IRuleReader.BoundTokenKey { get => m_boundTokenKey; set => m_boundTokenKey = value; }
    string? IRuleReader.BoundPreviousKey { get => m_boundPreviousKey; set => m_boundPreviousKey = value; }
    bool IRuleReader.TableKeyMissing { get => m_tableKeyMissing; set => m_tableKeyMissing = value; }
    Span<long> IRuleReader.PatternWord => m_frameHost.PatternWord;

    int IRuleReader.BoundIndex(BoundKey key) => Evaluator.BoundIndex(key: key);
    long IRuleReader.BindingValue(int ordinal) => Evaluator.BindingValue(ordinal: ordinal);
    CompiledTable IRuleReader.Table(int ordinal) => m_frameHost.Table(ordinal: ordinal);
    void IRuleReader.ReportTableKeyMissing(string table, long key) => Evaluator.ReportTableKeyMissing(table: table, key: key);
    Span<long> IRuleReader.BoardScratch(int cells) => m_frameHost.BoardScratch(cells: cells);
    bool IRuleReader.TryRowVersion(StateHandle row, out ulong version) => m_frameHost.TryRowVersion(row: row, version: out version);

    // IRuleHost — state mutation and preflight scoping ride the wrapped frame host's own door unchanged; this
    // reader widens only the world-operand reads below, never how a write lands.
    bool IRuleHost.TryApply(StateMutation mutation, ulong tick, bool preflight, out string reason) => m_frameHost.TryApply(mutation: mutation, tick: tick, preflight: preflight, reason: out reason);
    void IRuleHost.BeginPreflight() => m_frameHost.BeginPreflight();
    void IRuleHost.EndPreflight() => m_frameHost.EndPreflight();
    bool IRuleHost.TryCommitPreflight(ulong tick, out string reason) => m_frameHost.TryCommitPreflight(tick: tick, reason: out reason);
    EffectOutcome IRuleHost.FireEffect(EffectFact effect, string ruleName, ulong tick, ulong stepTicks, bool preflight) => EffectOutcome.Skipped;
    // No rule kind is this reader's own — a decision or interaction rule's own Decision/Interaction extension is
    // simply never consulted (the same posture a bare FrameHost already takes for every rule kind); its base
    // Gate/Effects still evaluate through the library's own plain gate-and-fire, exactly as they would for a rule
    // carrying no such extension at all.
    bool IRuleHost.TryEvaluateOwn(CompiledRule rule, RuleLatch latch, ulong tick, ulong stepTicks, out bool applied) {
        applied = false;

        return false;
    }
    void IRuleHost.RefusalRecorded(in RuleRuntimeDiagnostic diagnostic) { }

    // IWorldRuleReader — the sixteen world-operand facts, each the honest hostless answer this build's own
    // documentation already commits to for "no such body"/"no such machine"/"no such clock"/"no such link", applied
    // here for "there is no host at all": population 0 (WorldRuleFacts.Population), physics vacuously quiescent
    // (WorldRuleFacts.PhysicsQuiescent — "vacuously 1 when the world authors no rigid body"), no clock
    // (WorldRuleFacts.ClockPrefix — "a world with no music row has no clock and reads 0"), no region occupants
    // (WorldRuleFacts.RegionPrefix), no machine byte (WorldRuleFacts.MachinePrefix), no argmax/argmin/nearest body
    // (WorldRuleFacts.ArgMaxPrefix/NearestPrefix — "-1, no body"), the engine's largest distance for two bodies that
    // do not exist (WorldRuleFacts.DistancePrefix's own inverted-sentinel remarks; WorldServer.NoBodyDistance),
    // no sight line (WorldRuleFacts.LineOfSightPrefix), never parked (WorldRuleFacts.ParkedPrefix), perfectly
    // upright (WorldRuleFacts.UprightPrefix), a link never established (WorldRuleFacts.LinkPrefix), a zero channel
    // (WorldServer.ReadChannelValue's own "unseated seat reads Zero"), and no navigation state
    // (WorldPopulation.NavigationFact's own out-of-range-index case). PlacementInfluenceOperand alone reads Absent —
    // an unrepresented influence provider is UNKNOWABLE, never a falsely safe zero, exactly as
    // WorldServer.Influence.cs already answers it for the one real case that reads Absent today.
    RuleFact IWorldRuleReader.Read(PopulationOperand operand) => Record(operand: nameof(PopulationOperand), fact: RuleFact.Finite(value: 0L, kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(PhysicsQuiescentOperand operand) => Record(operand: nameof(PhysicsQuiescentOperand), fact: RuleFact.Finite(value: 1L, kind: CellKind.Bool));
    RuleFact IWorldRuleReader.Read(ClockOperand operand) => Record(operand: nameof(ClockOperand), fact: RuleFact.Finite(value: 0L, kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(RegionOccupancyOperand operand) => Record(operand: nameof(RegionOccupancyOperand), fact: RuleFact.Finite(value: 0L, kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(PlacementInfluenceOperand operand) => Record(operand: nameof(PlacementInfluenceOperand), fact: RuleFact.Absent(kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(MachineMemoryOperand operand) => Record(operand: nameof(MachineMemoryOperand), fact: RuleFact.Finite(value: 0L, kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(ArgBodyOperand operand) => Record(operand: nameof(ArgBodyOperand), fact: RuleFact.Finite(value: -1L, kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(BodyDistanceOperand operand) => Record(operand: nameof(BodyDistanceOperand), fact: RuleFact.Finite(value: FixedQ4816.MaxValue.Value, kind: CellKind.Fixed));
    RuleFact IWorldRuleReader.Read(LineOfSightOperand operand) => Record(operand: nameof(LineOfSightOperand), fact: RuleFact.Finite(value: 0L, kind: CellKind.Bool));
    RuleFact IWorldRuleReader.Read(ParkedOperand operand) => Record(operand: nameof(ParkedOperand), fact: RuleFact.Finite(value: 0L, kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(UprightOperand operand) => Record(operand: nameof(UprightOperand), fact: RuleFact.Finite(value: FixedQ4816.One.Value, kind: CellKind.Fixed));
    RuleFact IWorldRuleReader.Read(BodyFactOperand operand) => Record(operand: nameof(BodyFactOperand), fact: RuleFact.Finite(value: 0L, kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(LinkStalenessOperand operand) => Record(operand: nameof(LinkStalenessOperand), fact: RuleFact.Finite(value: 0L, kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(ChannelOperand operand) => Record(operand: nameof(ChannelOperand), fact: RuleFact.Finite(value: FixedQ4816.Zero.Value, kind: CellKind.Fixed));
    RuleFact IWorldRuleReader.Read(NearestOperand operand) => Record(operand: nameof(NearestOperand), fact: RuleFact.Finite(value: -1L, kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(NavigationOperand operand) => Record(operand: nameof(NavigationOperand), fact: RuleFact.Finite(value: 0L, kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(BoardCellOfOperand operand) => Record(operand: nameof(BoardCellOfOperand), fact: RuleFact.Finite(value: -1L, kind: CellKind.Int));
    // No body reference ever resolves — the same "-1 = no body" convention every read above already carries; a
    // $pair: key over two unresolved bodies spells through WorldServer.ResolvePairKey's own "a_b" convention with
    // both sides -1, never a special case.
    int IWorldRuleReader.ResolveBody(in CompiledBodyRef bodyRef) => -1;
    string IWorldRuleReader.PairKey(PairKeyFact key) => "-1_-1";

    private RuleFact Record(string operand, RuleFact fact) {
        if (m_hostFacts.Count < MaxHostFacts) {
            m_hostFacts.Add(item: new BrowserHostFact(
                Rule: Evaluator.RuleName,
                Operand: operand,
                Answer: RuleEvaluation.DescribeFact(value: fact.Value, kind: fact.Kind, isForever: fact.IsForever, isAbsent: fact.IsAbsent)
            ));
        }

        return fact;
    }
}
