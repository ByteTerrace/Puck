using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// Enumerates <see cref="WorldRuleFactKind"/> against <see cref="CompiledWorldOperand"/>'s case hierarchy: every
/// kind has exactly one case type, every case type derives from <see cref="WorldOperandFact"/> and reports its own
/// kind, and the carrier round-trips each case unchanged. This is the runtime exhaustiveness check the type-pattern
/// switches in <c>WorldServer.ReadWorldFact</c> and <c>WorldRuleWorkBudget.OperandCost</c> lean on until the real C#
/// union pattern gives the compiler one — a case added to one enumeration here without the other fails immediately.
/// </summary>
public sealed class WorldOperandUnionLawTests {
    private static readonly IReadOnlyDictionary<WorldRuleFactKind, Type> s_caseTypes = new Dictionary<WorldRuleFactKind, Type> {
        [WorldRuleFactKind.StateCell] = typeof(StateCellOperand),
        [WorldRuleFactKind.Tick] = typeof(TickOperand),
        [WorldRuleFactKind.Population] = typeof(PopulationOperand),
        [WorldRuleFactKind.PhysicsQuiescent] = typeof(PhysicsQuiescentOperand),
        [WorldRuleFactKind.RegionOccupancy] = typeof(RegionOccupancyOperand),
        [WorldRuleFactKind.MachineMemory] = typeof(MachineMemoryOperand),
        [WorldRuleFactKind.Reduction] = typeof(ReductionOperand),
        [WorldRuleFactKind.ArgBody] = typeof(ArgBodyOperand),
        [WorldRuleFactKind.BodyDistance] = typeof(BodyDistanceOperand),
        [WorldRuleFactKind.LineOfSight] = typeof(LineOfSightOperand),
        [WorldRuleFactKind.Parked] = typeof(ParkedOperand),
        [WorldRuleFactKind.Upright] = typeof(UprightOperand),
        [WorldRuleFactKind.LinkStaleness] = typeof(LinkStalenessOperand),
        [WorldRuleFactKind.Channel] = typeof(ChannelOperand),
        [WorldRuleFactKind.Nearest] = typeof(NearestOperand),
        [WorldRuleFactKind.Symmetry] = typeof(SymmetryOperand),
        [WorldRuleFactKind.Navigation] = typeof(NavigationOperand),
        [WorldRuleFactKind.Board] = typeof(BoardOperand),
        [WorldRuleFactKind.Phase] = typeof(PhaseOperand),
        [WorldRuleFactKind.Pattern] = typeof(PatternOperand),
        [WorldRuleFactKind.History] = typeof(HistoryOperand),
        [WorldRuleFactKind.Clock] = typeof(ClockOperand),
        [WorldRuleFactKind.Binding] = typeof(BindingOperand),
        [WorldRuleFactKind.Table] = typeof(TableOperand),
    };

    [Fact]
    public void EveryFactKind_HasExactlyOneRegisteredCaseType() {
        var kinds = Enum.GetValues<WorldRuleFactKind>();

        Assert.Equal(expected: kinds.Length, actual: s_caseTypes.Count);

        foreach (var kind in kinds) {
            Assert.True(condition: s_caseTypes.ContainsKey(kind), userMessage: $"{kind} has no registered case type");
        }
    }

    [Fact]
    public void EveryCaseType_IsSealedAndDerivesFromWorldOperandFact() {
        foreach (var type in s_caseTypes.Values) {
            Assert.True(condition: type.IsSealed, userMessage: $"{type.Name} must be sealed — a union case is never extended");
            Assert.True(condition: typeof(WorldOperandFact).IsAssignableFrom(type), userMessage: $"{type.Name} must derive from WorldOperandFact");
        }
    }

    [Fact]
    public void NoOtherCaseType_ExistsInTheAssembly() {
        var declared = typeof(WorldOperandFact).Assembly.GetTypes()
            .Where(candidate => typeof(WorldOperandFact).IsAssignableFrom(candidate) && !candidate.IsAbstract)
            .ToHashSet();

        Assert.Equal(expected: s_caseTypes.Values.ToHashSet(), actual: declared);
    }

    [Fact]
    public void EachCase_RoundTripsThroughTheCarrierReportingItsOwnKind() {
        AssertRoundTrips(new CompiledWorldOperand(new StateCellOperand(row: "row", key: "key", keyFrom: null, stateHandle: default, valueKind: CellKind.Int)), WorldRuleFactKind.StateCell);
        AssertRoundTrips(new CompiledWorldOperand(TickOperand.Instance), WorldRuleFactKind.Tick);
        AssertRoundTrips(new CompiledWorldOperand(PopulationOperand.Instance), WorldRuleFactKind.Population);
        AssertRoundTrips(new CompiledWorldOperand(PhysicsQuiescentOperand.Instance), WorldRuleFactKind.PhysicsQuiescent);
        AssertRoundTrips(new CompiledWorldOperand(new RegionOccupancyOperand(row: "placement")), WorldRuleFactKind.RegionOccupancy);
        AssertRoundTrips(new CompiledWorldOperand(new BindingOperand(ordinal: 1, name: "dealt", valueKind: CellKind.Int)), WorldRuleFactKind.Binding);
        AssertRoundTrips(new CompiledWorldOperand(new TableOperand(tableOrdinal: 0, table: "power", key: 7L, keyFrom: null, keyBinding: -1, column: 0, entryCount: 3, valueKind: CellKind.Int)), WorldRuleFactKind.Table);
        AssertRoundTrips(new CompiledWorldOperand(new MachineMemoryOperand(screen: 0, address: 0)), WorldRuleFactKind.MachineMemory);
        AssertRoundTrips(new CompiledWorldOperand(new ReductionOperand(row: "row", stateHandle: default, reduce: WorldStateReduceOp.Sum, filterRow: null, filterHandle: default, valueKind: CellKind.Int)), WorldRuleFactKind.Reduction);
        AssertRoundTrips(new CompiledWorldOperand(new ArgBodyOperand(row: "row", stateHandle: default, reduce: WorldStateReduceOp.Max, filterRow: null, filterHandle: default)), WorldRuleFactKind.ArgBody);

        var literalBody = new CompiledBodyRef(Kind: CompiledBodyRefKind.Literal, Index: 0, Row: null);

        AssertRoundTrips(new CompiledWorldOperand(new BodyDistanceOperand(literalBody, literalBody)), WorldRuleFactKind.BodyDistance);
        AssertRoundTrips(new CompiledWorldOperand(new LineOfSightOperand(literalBody, literalBody)), WorldRuleFactKind.LineOfSight);
        AssertRoundTrips(new CompiledWorldOperand(new ParkedOperand(literalBody)), WorldRuleFactKind.Parked);
        AssertRoundTrips(new CompiledWorldOperand(new UprightOperand(literalBody)), WorldRuleFactKind.Upright);
        AssertRoundTrips(new CompiledWorldOperand(new LinkStalenessOperand(row: "adjacency")), WorldRuleFactKind.LinkStaleness);
        AssertRoundTrips(new CompiledWorldOperand(new ChannelOperand(seat: 0, channelOrdinal: 0)), WorldRuleFactKind.Channel);
        AssertRoundTrips(new CompiledWorldOperand(new NearestOperand(literalBody, row: "row", stateHandle: default)), WorldRuleFactKind.Nearest);
        AssertRoundTrips(new CompiledWorldOperand(new SymmetryOperand(row: "row", key: null, keyFrom: null, stateHandle: default, symmetry: WorldSymmetryFunction.Ring, symmetryArgument: 0L, symmetryOtherCell: null, valueKind: CellKind.Int)), WorldRuleFactKind.Symmetry);
        AssertRoundTrips(new CompiledWorldOperand(new NavigationOperand(literalBody, row: "hasPath")), WorldRuleFactKind.Navigation);

        var boardQuery = new BoardNeighbourQuery(topology: null!, direction: 0);

        AssertRoundTrips(new CompiledWorldOperand(new BoardOperand(row: "row", key: null, keyFrom: null, stateHandle: default, board: boardQuery, bodyA: null)), WorldRuleFactKind.Board);
        AssertRoundTrips(new CompiledWorldOperand(new PhaseOperand(row: "row", stateHandle: default)), WorldRuleFactKind.Phase);
        AssertRoundTrips(new CompiledWorldOperand(new PatternOperand(row: "row", key: null, keyFrom: null, stateHandle: default, pattern: "pattern", board: null, filterRow: null, filterHandle: default, matchFacet: WorldMatchFacet.Accept, tokenExpression: null)), WorldRuleFactKind.Pattern);
        AssertRoundTrips(new CompiledWorldOperand(new HistoryOperand(row: "row", stateHandle: default, age: 0L, valueKind: CellKind.Int)), WorldRuleFactKind.History);
        AssertRoundTrips(new CompiledWorldOperand(ClockOperand.Instance), WorldRuleFactKind.Clock);
    }

    private static void AssertRoundTrips(CompiledWorldOperand operand, WorldRuleFactKind expected) {
        Assert.True(condition: operand.HasValue);
        Assert.Equal(expected: expected, actual: operand.Kind);
        Assert.Same(expected: operand.Value, actual: operand.Value);
        Assert.Same(expected: s_caseTypes[expected], actual: operand.Value!.GetType());
    }
}
