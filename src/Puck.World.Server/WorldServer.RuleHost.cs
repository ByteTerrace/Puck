using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The server as the rule evaluator's host: the state library's <see cref="IRuleHost"/> over the installed
/// definition — its reader forwards the evaluation in flight from the evaluator, its mutation door maps a
/// <see cref="StateMutation"/> onto the world's own, and it widens the reader to the world facts only the server can
/// answer.</summary>
public sealed partial class WorldServer : IWorldRuleReader, IRuleHost {
    private long[] m_boardScratch = [];

    ulong IRuleReader.Tick => m_evaluator.Tick;
    IReadOnlyList<StateRow> IRuleReader.Rows => m_definition.State;
    StateCatalog IRuleReader.Catalog => m_definition.StateCatalog;
    CompiledPatterns IRuleReader.Patterns => m_patterns;
    string? IRuleReader.BoundEachKey => m_evaluator.BoundEachKey;
    string? IRuleReader.BoundTokenKey {
        get => m_patternTokenKey;
        set => m_patternTokenKey = value;
    }
    string? IRuleReader.BoundPreviousKey {
        get => m_patternPreviousKey;
        set => m_patternPreviousKey = value;
    }
    bool IRuleReader.TableKeyMissing {
        get => m_tableKeyMissing;
        set => m_tableKeyMissing = value;
    }
    Span<long> IRuleReader.PatternWord => m_patternWord;

    int IRuleReader.BoundIndex(BoundKey key) => m_evaluator.BoundIndex(key: key);
    long IRuleReader.BindingValue(int ordinal) => m_evaluator.BindingValue(ordinal: ordinal);
    CompiledTable IRuleReader.Table(int ordinal) => m_tables[ordinal];
    void IRuleReader.ReportTableKeyMissing(string table, long key) => m_evaluator.ReportTableKeyMissing(table: table, key: key);
    Span<long> IRuleReader.BoardScratch(int cells) => BoardScratch(count: cells);

    bool IRuleHost.TryApply(StateMutation mutation, ulong tick, bool preflight, out string reason) => TryApplyRuleMutation(
        mutation: mutation switch {
            StateMutation.UpsertCell cell => new WorldMutation.UpsertStateCell(
                Principal: WorldPrincipal.World,
                Row: cell.Row,
                Key: cell.Key,
                Value: cell.Value,
                Kind: ((cell.Write == StateWriteKind.Add) ? WorldDocumentWriteKind.Add : WorldDocumentWriteKind.Set),
                Text: cell.Text
            ),
            StateMutation.RemoveCell cell => new WorldMutation.RemoveStateCell(Principal: WorldPrincipal.World, Row: cell.Row, Key: cell.Key),
            StateMutation.Generate generate => new WorldMutation.Generate(Principal: WorldPrincipal.World, Row: generate.Row),
            StateMutation.Apply apply => new WorldMutation.TransformState(WorldPrincipal.World, apply.Transform),
            _ => throw new InvalidOperationException(message: $"state mutation '{mutation.GetType().Name}' has no world mapping."),
        },
        tick: tick,
        preflight: preflight,
        reason: out reason
    );
    void IRuleHost.BeginPreflight() {
        m_preflightScopes.Push(item: m_definition);
        m_preflightMutations.Push(item: []);
    }
    void IRuleHost.EndPreflight() {
        m_definition = m_preflightScopes.Pop();
        _ = m_preflightMutations.Pop();
    }
    // One member installs as itself; several install as one Batch — one admission, validation, journal entry, and
    // delivery for the whole transaction.
    bool IRuleHost.TryCommitPreflight(ulong tick, out string reason) {
        m_definition = m_preflightScopes.Pop();
        var composed = m_preflightMutations.Pop();
        reason = string.Empty;

        if (composed.Count == 0) {
            return false;
        }

        var mutation = ((composed.Count == 1) ? composed[0] : new WorldMutation.Batch(Principal: WorldPrincipal.World, Mutations: composed));

        if (TryApplyMutation(mutation: mutation, tick: tick, connectionId: SubmissionEnvelope.LocalConnectionId, correlationId: 0, preMetered: false)) {
            return true;
        }

        reason = "the ordinary mutation door refused the transaction; its mutation rejection names the concrete reason";

        return false;
    }
    // A decision evaluates on its own timers; an interaction evaluates once per bound carrier or pair, each through
    // the evaluator's own gate-and-fire under the latch binding the sweep chooses.
    bool IRuleHost.TryEvaluateOwn(CompiledRule rule, RuleLatch latch, ulong tick, ulong stepTicks, out bool applied) {
        var world = (CompiledWorldRule)rule;

        if (world.Decision is not null) {
            applied = EvaluateDecisionRule(rule: world, tick: tick, stepTicks: stepTicks);

            return true;
        }

        if (world.Interaction is { } interaction) {
            applied = EvaluateInteraction(rule: world, interaction: interaction, latch: latch, bindings: latch.Bindings(name: rule.Name), tick: tick, stepTicks: stepTicks);

            return true;
        }

        applied = false;

        return false;
    }
    // One line per category per server lifetime; the ledger keeps the exact count.
    void IRuleHost.RefusalRecorded(in RuleRuntimeDiagnostic diagnostic) =>
        Console.Error.WriteLine(value: $"[world.rule: effect refused ({diagnostic.Refusal}) — rule '{diagnostic.Rule}', '{diagnostic.Effect}': {diagnostic.Detail}; world.rule.failures carries the running count]");

    RuleFact IWorldRuleReader.Read(PopulationOperand operand) => RuleFact.Finite(value: m_population.ActiveCount(), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(PhysicsQuiescentOperand operand) => RuleFact.Finite(value: (m_population.RigidBodiesQuiescent() ? 1L : 0L), kind: CellKind.Bool);
    RuleFact IWorldRuleReader.Read(ClockOperand operand) => RuleFact.Finite(value: ReadClockPhaseError(), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(RegionOccupancyOperand operand) => RuleFact.Finite(value: m_events.OccupantCount(placementId: operand.PlacementId), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(MachineMemoryOperand operand) => RuleFact.Finite(value: (Machines.TryPeek(screen: operand.Screen, address: operand.Address, out var raw) ? raw : (byte)0), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(ArgBodyOperand operand) => RuleFact.Finite(
        value: ResolveArgBody(handle: operand.StateHandle, op: operand.Reduce, tick: m_evaluator.Tick, filterHandle: operand.FilterHandle, hasFilter: (operand.FilterRow is not null)),
        kind: CellKind.Int
    );
    RuleFact IWorldRuleReader.Read(BodyDistanceOperand operand) => RuleFact.Finite(value: ReadBodyDistance(bodyA: operand.BodyA, bodyB: operand.BodyB, tick: m_evaluator.Tick));
    RuleFact IWorldRuleReader.Read(LineOfSightOperand operand) => RuleFact.Finite(value: (ReadBodyLineOfSight(bodyA: operand.BodyA, bodyB: operand.BodyB, tick: m_evaluator.Tick) ? 1L : 0L), kind: CellKind.Bool);
    RuleFact IWorldRuleReader.Read(ParkedOperand operand) => ((ReadParkedRemaining(bodyRef: operand.BodyA, tick: m_evaluator.Tick) is { } remaining)
        ? RuleFact.Finite(value: remaining, kind: CellKind.Int)
        : RuleFact.Forever(kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(UprightOperand operand) => RuleFact.Finite(value: ReadBodyUpright(bodyRef: operand.BodyA, tick: m_evaluator.Tick));
    RuleFact IWorldRuleReader.Read(LinkStalenessOperand operand) => RuleFact.Finite(value: m_events.LinkStalenessTicks(adjacencyName: operand.AdjacencyName), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(ChannelOperand operand) => RuleFact.Finite(value: ReadChannelValue(seat: operand.Seat, ordinal: operand.ChannelOrdinal));
    RuleFact IWorldRuleReader.Read(NearestOperand operand) => RuleFact.Finite(value: ResolveNearestBody(from: operand.BodyA, tagRowHandle: operand.StateHandle, tick: m_evaluator.Tick), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(NavigationOperand operand) => RuleFact.Finite(
        value: m_population.NavigationFact(index: ResolveBodyRef(bodyRef: operand.BodyA, tick: m_evaluator.Tick), facet: operand.Facet),
        kind: CellKind.Int
    );
    RuleFact IWorldRuleReader.Read(BoardCellOfOperand operand) {
        var index = ResolveBodyRef(bodyRef: operand.BodyA, tick: m_evaluator.Tick);

        return RuleFact.Finite(
            value: (((Body(index: index) is { } body) && operand.Topology.TryCellOf(position: body.FixedPosition, cell: out var cell)) ? cell : -1),
            kind: CellKind.Int
        );
    }
    int IWorldRuleReader.ResolveBody(in CompiledBodyRef bodyRef) => ResolveBodyRef(bodyRef: bodyRef, tick: m_evaluator.Tick);
    string IWorldRuleReader.PairKey(PairKeyFact key) => ResolvePairKey(a: ResolveBodyRef(bodyRef: key.BodyA, tick: m_evaluator.Tick), b: ResolveBodyRef(bodyRef: key.BodyB, tick: m_evaluator.Tick));

    private Span<long> BoardScratch(int count) {
        if (m_boardScratch.Length < count) {
            m_boardScratch = new long[Math.Max(val1: count, val2: BoardMask.MaxCells)];
        }

        return m_boardScratch.AsSpan(start: 0, length: count);
    }

    private RuleFact ReadWorldFact(OperandFact operand, ulong tick) => m_evaluator.Read(operand: operand, tick: tick);
    private string ResolveOperandKey(string? key, CompiledCellRef? keyFrom, ulong tick) => m_evaluator.ResolveKey(key: key, keyFrom: keyFrom, tick: tick);
    private bool TryEvaluateExpression(CompiledExpressionToken[] program, CellKind kind, ulong tick, out long value) => m_evaluator.TryEvaluateExpression(program: program, kind: kind, tick: tick, value: out value);
    // A gate read outside the evaluator's own loop (a decision's option or interrupt) reports against the rule the
    // decision path named.
    private bool RuleGateOpen(GateToken[] gate, ulong tick) => m_evaluator.GateOpen(gate: gate, tick: tick, ruleName: m_evaluator.RuleName);
}
