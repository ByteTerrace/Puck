namespace Puck.World.Server;

/// <summary>The server as the rule evaluator's reader: the state library's <see cref="IRuleReader"/> over the
/// installed definition and the evaluation in flight, widened to the world facts only the server can answer.
/// <see cref="m_ruleTick"/> is the tick every read answers as of; each entry point that takes a tick sets it before
/// reading.</summary>
public sealed partial class WorldServer : IWorldRuleReader {
    private ulong m_ruleTick;
    private long[] m_boardScratch = [];

    ulong IRuleReader.Tick => m_ruleTick;
    IReadOnlyList<StateRow> IRuleReader.Rows => m_definition.State;
    StateCatalog IRuleReader.Catalog => m_definition.StateCatalog;
    CompiledPatterns IRuleReader.Patterns => m_patterns;
    string? IRuleReader.BoundEachKey => m_boundEachKey;
    string? IRuleReader.BoundTokenKey {
        get => m_patternTokenKey;
        set => m_patternTokenKey = value;
    }
    bool IRuleReader.TableKeyMissing {
        get => m_tableKeyMissing;
        set => m_tableKeyMissing = value;
    }
    Span<long> IRuleReader.PatternWord => m_patternWord;

    int IRuleReader.BoundIndex(BoundKey key) => BoundBody(binding: key);
    long IRuleReader.BindingValue(int ordinal) => m_ruleBindingValues[ordinal];
    CompiledTable IRuleReader.Table(int ordinal) => m_tables[ordinal];
    void IRuleReader.ReportTableKeyMissing(string table, long key) {
        m_tableKeyMissing = true;
        ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.TableKeyMissing, ruleName: m_tableKeyMissingRule, effect: $"$table:{table}", tick: m_ruleTick, detail: $"key {key} is not an entry of table '{table}'");
    }
    Span<long> IRuleReader.BoardScratch(int cells) => BoardScratch(count: cells);

    RuleFact IWorldRuleReader.Read(PopulationOperand operand) => RuleFact.Finite(value: m_population.ActiveCount(), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(PhysicsQuiescentOperand operand) => RuleFact.Finite(value: (m_population.RigidBodiesQuiescent() ? 1L : 0L), kind: CellKind.Bool);
    RuleFact IWorldRuleReader.Read(ClockOperand operand) => RuleFact.Finite(value: ReadClockPhaseError(), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(RegionOccupancyOperand operand) => RuleFact.Finite(value: m_events.OccupantCount(placementId: operand.PlacementId), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(MachineMemoryOperand operand) => RuleFact.Finite(value: (Machines.TryPeek(screen: operand.Screen, address: operand.Address, out var raw) ? raw : (byte)0), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(ArgBodyOperand operand) => RuleFact.Finite(
        value: ResolveArgBody(handle: operand.StateHandle, op: operand.Reduce, tick: m_ruleTick, filterHandle: operand.FilterHandle, hasFilter: (operand.FilterRow is not null)),
        kind: CellKind.Int
    );
    RuleFact IWorldRuleReader.Read(BodyDistanceOperand operand) => RuleFact.Finite(value: ReadBodyDistance(bodyA: operand.BodyA, bodyB: operand.BodyB, tick: m_ruleTick));
    RuleFact IWorldRuleReader.Read(LineOfSightOperand operand) => RuleFact.Finite(value: (ReadBodyLineOfSight(bodyA: operand.BodyA, bodyB: operand.BodyB, tick: m_ruleTick) ? 1L : 0L), kind: CellKind.Bool);
    RuleFact IWorldRuleReader.Read(ParkedOperand operand) => ((ReadParkedRemaining(bodyRef: operand.BodyA, tick: m_ruleTick) is { } remaining)
        ? RuleFact.Finite(value: remaining, kind: CellKind.Int)
        : RuleFact.Forever(kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(UprightOperand operand) => RuleFact.Finite(value: ReadBodyUpright(bodyRef: operand.BodyA, tick: m_ruleTick));
    RuleFact IWorldRuleReader.Read(LinkStalenessOperand operand) => RuleFact.Finite(value: m_events.LinkStalenessTicks(adjacencyName: operand.AdjacencyName), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(ChannelOperand operand) => RuleFact.Finite(value: ReadChannelValue(seat: operand.Seat, ordinal: operand.ChannelOrdinal));
    RuleFact IWorldRuleReader.Read(NearestOperand operand) => RuleFact.Finite(value: ResolveNearestBody(from: operand.BodyA, tagRowHandle: operand.StateHandle, tick: m_ruleTick), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(NavigationOperand operand) => RuleFact.Finite(
        value: m_population.NavigationFact(index: ResolveBodyRef(bodyRef: operand.BodyA, tick: m_ruleTick), facet: operand.Facet),
        kind: CellKind.Int
    );
    RuleFact IWorldRuleReader.Read(BoardCellOfOperand operand) {
        var index = ResolveBodyRef(bodyRef: operand.BodyA, tick: m_ruleTick);

        return RuleFact.Finite(
            value: (((Body(index: index) is { } body) && operand.Topology.TryCellOf(position: body.FixedPosition, cell: out var cell)) ? cell : -1),
            kind: CellKind.Int
        );
    }
    int IWorldRuleReader.ResolveBody(in CompiledBodyRef bodyRef) => ResolveBodyRef(bodyRef: bodyRef, tick: m_ruleTick);
    string IWorldRuleReader.PairKey(PairKeyFact key) => ResolvePairKey(a: ResolveBodyRef(bodyRef: key.BodyA, tick: m_ruleTick), b: ResolveBodyRef(bodyRef: key.BodyB, tick: m_ruleTick));

    private Span<long> BoardScratch(int count) {
        if (m_boardScratch.Length < count) {
            m_boardScratch = new long[Math.Max(val1: count, val2: BoardMask.MaxCells)];
        }

        return m_boardScratch.AsSpan(start: 0, length: count);
    }

    private RuleFact ReadWorldFact(OperandFact operand, ulong tick) {
        m_ruleTick = tick;

        return operand.Read(reader: this);
    }

    private string ResolveOperandKey(string? key, CompiledCellRef? keyFrom, ulong tick) {
        m_ruleTick = tick;

        return RuleEvaluation.ResolveKey(reader: this, key: key, keyFrom: keyFrom);
    }

    private bool TryEvaluateExpression(CompiledExpressionToken[] program, CellKind kind, ulong tick, out long value) {
        m_ruleTick = tick;

        return RuleEvaluation.TryEvaluateExpression(reader: this, program: program, kind: kind, value: out value);
    }

    private bool RuleGateOpen(GateToken[] gate, ulong tick) {
        m_ruleTick = tick;
        m_tableKeyMissing = false;

        var open = RuleEvaluation.GateHolds(reader: this, gate: gate, trace: m_gateTrace);

        if (m_tableKeyMissing) {
            m_tableKeyMissing = false;

            return false;
        }

        return open;
    }
}
