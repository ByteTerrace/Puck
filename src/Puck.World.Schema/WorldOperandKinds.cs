namespace Puck.World;

/// <summary>The operand facts only a world answers. Each reads through <see cref="IWorldRuleReader"/>, the widening
/// of <see cref="IRuleReader"/> the world's evaluator implements; the compile context every cost consults is a
/// <see cref="WorldRuleCompileContext"/>.</summary>
public abstract class WorldOperandFact : OperandFact {
    private protected WorldOperandFact(CellKind valueKind) : base(valueKind: valueKind) { }

    /// <inheritdoc/>
    public override bool HostOnly => true;

    /// <summary>Returns the population capacity the context's document declares — the per-tick cost of a read that
    /// scans every population slot.</summary>
    /// <param name="context">The compile context.</param>
    protected static long PopulationCapacity(RuleCompileContext context) => ((WorldRuleCompileContext)context).Definition.Population.Capacity;
}

/// <summary>The active body count (<see cref="WorldRuleFacts.Population"/>).</summary>
public sealed class PopulationOperand : WorldOperandFact {
    public static readonly PopulationOperand Instance = new();
    private PopulationOperand() : base(CellKind.Int) { }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => 1L;
}

/// <summary>Whether every rigid body is at rest (<see cref="WorldRuleFacts.PhysicsQuiescent"/>); scans every
/// population slot, so it is priced at capacity.</summary>
public sealed class PhysicsQuiescentOperand : WorldOperandFact {
    public static readonly PhysicsQuiescentOperand Instance = new();
    private PhysicsQuiescentOperand() : base(CellKind.Bool) { }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => PopulationCapacity(context: context);
}

/// <summary>The music clock's phase error (<see cref="WorldRuleFacts.ClockPrefix"/>).</summary>
public sealed class ClockOperand : WorldOperandFact {
    public static readonly ClockOperand Instance = new();
    private ClockOperand() : base(CellKind.Int) { }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => 1L;
}

/// <summary>The occupant count of a region placement (<see cref="WorldRuleFacts.RegionPrefix"/>).</summary>
public sealed class RegionOccupancyOperand : WorldOperandFact {
    public RegionOccupancyOperand(string placementId) : base(CellKind.Int) => PlacementId = placementId;

    public string PlacementId { get; }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => PopulationCapacity(context: context);
}

/// <summary>One byte of a screen's machine memory (<see cref="WorldRuleFacts.MachinePrefix"/>).</summary>
public sealed class MachineMemoryOperand : WorldOperandFact {
    public MachineMemoryOperand(int screen, int address) : base(CellKind.Int) {
        Screen = screen;
        Address = address;
    }

    public int Screen { get; }
    public int Address { get; }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => 1L;
}

/// <summary>The body whose keyed-row cell is the extreme (<see cref="WorldRuleFacts.ArgMaxPrefix"/> /
/// <see cref="WorldRuleFacts.ArgMinPrefix"/>), optionally filtered by a second keyed row.</summary>
public sealed class ArgBodyOperand : WorldOperandFact {
    public ArgBodyOperand(string row, StateHandle stateHandle, StateReduceOp reduce, string? filterRow, StateHandle filterHandle) : base(CellKind.Int) {
        Row = row;
        StateHandle = stateHandle;
        Reduce = reduce;
        FilterRow = filterRow;
        FilterHandle = filterHandle;
    }

    public string Row { get; }
    public StateHandle StateHandle { get; }
    public StateReduceOp Reduce { get; }
    public string? FilterRow { get; }
    public StateHandle FilterHandle { get; }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => context.RowCapacity(name: Row);
    public override void CollectReads(List<RuleAccess> into) {
        into.Add(item: new RuleAccess(Row: Row, Key: null));
        if (FilterRow is { } filter) {
            into.Add(item: new RuleAccess(Row: filter, Key: null));
        }
    }
}

/// <summary>The distance between two bodies (<see cref="WorldRuleFacts.DistancePrefix"/>).</summary>
public sealed class BodyDistanceOperand : WorldOperandFact {
    public BodyDistanceOperand(CompiledBodyRef bodyA, CompiledBodyRef bodyB) : base(CellKind.Fixed) {
        BodyA = bodyA;
        BodyB = bodyB;
    }

    public CompiledBodyRef BodyA { get; }
    public CompiledBodyRef BodyB { get; }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => 1L;
}

/// <summary>Whether two bodies see each other (<see cref="WorldRuleFacts.LineOfSightPrefix"/>).</summary>
public sealed class LineOfSightOperand : WorldOperandFact {
    public LineOfSightOperand(CompiledBodyRef bodyA, CompiledBodyRef bodyB) : base(CellKind.Bool) {
        BodyA = bodyA;
        BodyB = bodyB;
    }

    public CompiledBodyRef BodyA { get; }
    public CompiledBodyRef BodyB { get; }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => 1L;
}

/// <summary>The ticks a parked body has left, or forever (<see cref="WorldRuleFacts.ParkedPrefix"/>).</summary>
public sealed class ParkedOperand : WorldOperandFact {
    public ParkedOperand(CompiledBodyRef bodyA) : base(CellKind.Int) => BodyA = bodyA;

    public CompiledBodyRef BodyA { get; }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => 1L;
}

/// <summary>A body's uprightness (<see cref="WorldRuleFacts.UprightPrefix"/>).</summary>
public sealed class UprightOperand : WorldOperandFact {
    public UprightOperand(CompiledBodyRef bodyA) : base(CellKind.Fixed) => BodyA = bodyA;

    public CompiledBodyRef BodyA { get; }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => 1L;
}

/// <summary>One fact on the identity a body drives under, read from the body's lane cell in the world's reserved
/// <see cref="WorldIdentityFactLane"/> row (<see cref="WorldRuleFacts.IdentityPrefix"/>); a body driving under no
/// identity, or a fact the identity never wrote, reads 0.</summary>
public sealed class IdentityFactOperand : WorldOperandFact {
    private readonly CellName[] m_laneKeys;

    public IdentityFactOperand(CompiledBodyRef body, string fact, StateHandle lane, int capacity) : base(CellKind.Int) {
        Body = body;
        Fact = fact;
        Lane = lane;
        m_laneKeys = new CellName[capacity];
    }

    public CompiledBodyRef Body { get; }
    public string Fact { get; }
    public StateHandle Lane { get; }

    public override RuleFact Read(IRuleReader reader) {
        var body = Body;
        var index = ((IWorldRuleReader)reader).ResolveBody(bodyRef: in body);
        var store = reader.Store;

        if (
            (index < 0) ||
            !reader.Catalog.TryGetDescriptor(handle: Lane, descriptor: out var descriptor) ||
            (((uint)descriptor.LaneOrdinal) >= ((uint)store.Rows.Count))
        ) {
            return RuleFact.Finite(value: 0L, kind: CellKind.Int);
        }

        return RuleFact.Finite(value: (store.TryStored(row: store.Rows[descriptor.LaneOrdinal], key: LaneKey(bodyIndex: index), value: out var value, text: out _) ? value : 0L), kind: CellKind.Int);
    }
    public override long Cost(RuleCompileContext context) => 1L;
    public override void CollectReads(List<RuleAccess> into) => into.Add(item: new RuleAccess(Row: WorldIdentityFactLane.RowName, Key: null));

    private CellName LaneKey(int bodyIndex) {
        if (((uint)bodyIndex) >= ((uint)m_laneKeys.Length)) {
            return CellName.Parse(candidate: WorldIdentityFactLane.Key(bodyIndex: bodyIndex, fact: Fact));
        }
        if (m_laneKeys[bodyIndex].Value is null) {
            m_laneKeys[bodyIndex] = CellName.Parse(candidate: WorldIdentityFactLane.Key(bodyIndex: bodyIndex, fact: Fact));
        }

        return m_laneKeys[bodyIndex];
    }
}

/// <summary>The staleness of an adjacency link, in ticks (<see cref="WorldRuleFacts.LinkPrefix"/>).</summary>
public sealed class LinkStalenessOperand : WorldOperandFact {
    public LinkStalenessOperand(string adjacencyName) : base(CellKind.Int) => AdjacencyName = adjacencyName;

    public string AdjacencyName { get; }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => 1L;
}

/// <summary>A seat's live composition-channel value (<see cref="WorldRuleFacts.ChannelPrefix"/>).</summary>
public sealed class ChannelOperand : WorldOperandFact {
    public ChannelOperand(int seat, int channelOrdinal) : base(CellKind.Fixed) {
        Seat = seat;
        ChannelOrdinal = channelOrdinal;
    }

    /// <summary>Gets the zero-based seat.</summary>
    public int Seat { get; }
    public int ChannelOrdinal { get; }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => 1L;
}

/// <summary>The nearest body carrying a tag-row cell (<see cref="WorldRuleFacts.NearestPrefix"/>); scans every
/// population slot.</summary>
public sealed class NearestOperand : WorldOperandFact {
    public NearestOperand(CompiledBodyRef bodyA, string row, StateHandle stateHandle) : base(CellKind.Int) {
        BodyA = bodyA;
        Row = row;
        StateHandle = stateHandle;
    }

    public CompiledBodyRef BodyA { get; }
    public string Row { get; }
    public StateHandle StateHandle { get; }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => PopulationCapacity(context: context);
    public override void CollectReads(List<RuleAccess> into) => into.Add(item: new RuleAccess(Row: Row, Key: null));
}

/// <summary>One facet of a body's navigation state (<see cref="WorldRuleFacts.NavigationPrefix"/>).</summary>
public sealed class NavigationOperand : WorldOperandFact {
    public NavigationOperand(CompiledBodyRef bodyA, string facet) : base(CellKind.Int) {
        BodyA = bodyA;
        Facet = facet;
    }

    public CompiledBodyRef BodyA { get; }
    public string Facet { get; }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => 1L;
}

/// <summary>The board cell under a body (<c>$board:cellOf:&lt;row&gt;:&lt;bodyRef&gt;</c>), or -1 off the board — the one
/// board query that needs a body's position, so it is the world's rather than the library's.</summary>
public sealed class BoardCellOfOperand : WorldOperandFact {
    public BoardCellOfOperand(string row, CompiledTopology topology, CompiledBodyRef bodyA) : base(CellKind.Int) {
        Row = row;
        Topology = topology;
        BodyA = bodyA;
    }

    public string Row { get; }
    public CompiledTopology Topology { get; }
    public CompiledBodyRef BodyA { get; }

    public override RuleFact Read(IRuleReader reader) => ((IWorldRuleReader)reader).Read(operand: this);
    public override long Cost(RuleCompileContext context) => Topology.CellCount;
}

/// <summary>A <c>$pair:&lt;bodyRefA&gt;:&lt;bodyRefB&gt;</c> cell key, resolved from the two bodies' indices at evaluation.</summary>
public sealed class PairKeyFact : KeyFact {
    public PairKeyFact(CompiledBodyRef bodyA, CompiledBodyRef bodyB) {
        BodyA = bodyA;
        BodyB = bodyB;
    }

    public CompiledBodyRef BodyA { get; }
    public CompiledBodyRef BodyB { get; }

    public override string Resolve(IRuleReader reader) => ((IWorldRuleReader)reader).PairKey(key: this);
    /// <inheritdoc/>
    public override bool HostOnly => true;
}
