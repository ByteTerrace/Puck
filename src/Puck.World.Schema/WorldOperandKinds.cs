using Puck.Physics.Motion;
namespace Puck.World;

/// <summary>The payload of one operand fact only a world answers — what a <see cref="WorldFactOperand"/> hands to
/// <see cref="IWorldFacts"/>. A payload carries the read's own addressing and nothing about performing it: the
/// compiled operand owns the cost, the read set and the facet.</summary>
public abstract class WorldOperandFact {
    private protected WorldOperandFact(CellKind valueKind) => ValueKind = valueKind;

    /// <summary>Gets the raw encoding this operand's value is returned in.</summary>
    public CellKind ValueKind { get; }
}
/// <summary>The active body count (<see cref="WorldRuleFacts.Population"/>).</summary>
public sealed class PopulationOperand : WorldOperandFact {
    public static readonly PopulationOperand Instance = new();

    private PopulationOperand() : base(CellKind.Int) { }
}
/// <summary>Whether every rigid body is at rest (<see cref="WorldRuleFacts.PhysicsQuiescent"/>); scans every
/// population slot, so it is priced at capacity.</summary>
public sealed class PhysicsQuiescentOperand : WorldOperandFact {
    public static readonly PhysicsQuiescentOperand Instance = new();

    private PhysicsQuiescentOperand() : base(CellKind.Bool) { }
}
/// <summary>The music clock's phase error (<see cref="WorldRuleFacts.ClockPrefix"/>).</summary>
public sealed class ClockOperand : WorldOperandFact {
    public static readonly ClockOperand Instance = new();

    private ClockOperand() : base(CellKind.Int) { }
}
/// <summary>The occupant count of a region placement (<see cref="WorldRuleFacts.RegionPrefix"/>).</summary>
public sealed class RegionOccupancyOperand : WorldOperandFact {
    public RegionOccupancyOperand(string placementId) : base(CellKind.Int) => PlacementId = placementId;

    public string PlacementId { get; }
}
/// <summary>One byte of a screen's machine memory (<see cref="WorldRuleFacts.MachinePrefix"/>).</summary>
public sealed class MachineMemoryOperand : WorldOperandFact {
    public MachineMemoryOperand(int screen, int address) : base(CellKind.Int) {
        Screen = screen;
        Address = address;
    }

    public int Address { get; }
    public int Screen { get; }
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

    public StateHandle FilterHandle { get; }
    public string? FilterRow { get; }
    public StateReduceOp Reduce { get; }
    public string Row { get; }
    public StateHandle StateHandle { get; }
}
/// <summary>The distance between two bodies (<see cref="WorldRuleFacts.DistancePrefix"/>).</summary>
public sealed class BodyDistanceOperand : WorldOperandFact {
    public BodyDistanceOperand(CompiledBodyRef bodyA, CompiledBodyRef bodyB) : base(CellKind.Fixed) {
        BodyA = bodyA;
        BodyB = bodyB;
    }

    public CompiledBodyRef BodyA { get; }
    public CompiledBodyRef BodyB { get; }
}
/// <summary>Whether two bodies see each other (<see cref="WorldRuleFacts.LineOfSightPrefix"/>).</summary>
public sealed class LineOfSightOperand : WorldOperandFact {
    public LineOfSightOperand(CompiledBodyRef bodyA, CompiledBodyRef bodyB) : base(CellKind.Bool) {
        BodyA = bodyA;
        BodyB = bodyB;
    }

    public CompiledBodyRef BodyA { get; }
    public CompiledBodyRef BodyB { get; }
}
/// <summary>The ticks a parked body has left, or forever (<see cref="WorldRuleFacts.ParkedPrefix"/>).</summary>
public sealed class ParkedOperand : WorldOperandFact {
    public ParkedOperand(CompiledBodyRef bodyA) : base(CellKind.Int) => BodyA = bodyA;

    public CompiledBodyRef BodyA { get; }
}
/// <summary>A body's uprightness (<see cref="WorldRuleFacts.UprightPrefix"/>).</summary>
public sealed class UprightOperand : WorldOperandFact {
    public UprightOperand(CompiledBodyRef bodyA) : base(CellKind.Fixed) => BodyA = bodyA;

    public CompiledBodyRef BodyA { get; }
}
/// <summary>One live body fact (<see cref="WorldRuleFacts.FactPrefix"/>): <c>1</c> while the body's fact bit holds,
/// <c>0</c> otherwise or for a reference resolving to no live body.</summary>
public sealed class BodyFactOperand : WorldOperandFact {
    public BodyFactOperand(CompiledBodyRef body, BodyFacts fact) : base(CellKind.Int) {
        Body = body;
        Fact = fact;
    }

    public CompiledBodyRef Body { get; }
    public BodyFacts Fact { get; }
}
/// <summary>One fact on the identity a body drives under, read from the body's lane cell in the world's reserved
/// <see cref="WorldIdentityFactLane"/> row (<see cref="WorldRuleFacts.IdentityPrefix"/>); a body driving under no
/// identity, or a fact the identity never wrote, reads 0.</summary>
public sealed class IdentityFactOperand : WorldOperandFact {
    public IdentityFactOperand(CompiledBodyRef body, string fact, StateHandle lane) : base(CellKind.Int) {
        Body = body;
        Fact = fact;
        Lane = lane;
    }

    public CompiledBodyRef Body { get; }
    public string Fact { get; }
    public StateHandle Lane { get; }
}
/// <summary>The staleness of an adjacency link, in ticks (<see cref="WorldRuleFacts.LinkPrefix"/>).</summary>
public sealed class LinkStalenessOperand : WorldOperandFact {
    public LinkStalenessOperand(string adjacencyName) : base(CellKind.Int) => AdjacencyName = adjacencyName;

    public string AdjacencyName { get; }
}
/// <summary>A seat's live composition-channel value (<see cref="WorldRuleFacts.ChannelPrefix"/>).</summary>
public sealed class ChannelOperand : WorldOperandFact {
    public ChannelOperand(int seat, int channelOrdinal) : base(CellKind.Fixed) {
        Seat = seat;
        ChannelOrdinal = channelOrdinal;
    }

    public int ChannelOrdinal { get; }
    /// <summary>Gets the zero-based seat.</summary>
    public int Seat { get; }
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
}
/// <summary>One facet of a body's navigation state (<see cref="WorldRuleFacts.NavigationPrefix"/>).</summary>
public sealed class NavigationOperand : WorldOperandFact {
    public NavigationOperand(CompiledBodyRef bodyA, string facet) : base(CellKind.Int) {
        BodyA = bodyA;
        Facet = facet;
    }

    public CompiledBodyRef BodyA { get; }
    public string Facet { get; }
}
/// <summary>The board cell under a body (<c>$board:cellOf:&lt;row&gt;:&lt;bodyRef&gt;</c>), or -1 off the board — the one
/// board query that needs a body's position, so it is the world's rather than the library's.</summary>
public sealed class BoardCellOfOperand : WorldOperandFact {
    public BoardCellOfOperand(string row, CompiledTopology topology, CompiledBodyRef bodyA) : base(CellKind.Int) {
        Row = row;
        Topology = topology;
        BodyA = bodyA;
    }

    public CompiledBodyRef BodyA { get; }
    public string Row { get; }
    public CompiledTopology Topology { get; }
}
/// <summary>A <c>$pair:&lt;bodyRefA&gt;:&lt;bodyRefB&gt;</c> cell key, resolved from the two bodies' indices at evaluation.</summary>
public sealed class PairKeyFact {
    public PairKeyFact(CompiledBodyRef bodyA, CompiledBodyRef bodyB) {
        BodyA = bodyA;
        BodyB = bodyB;
    }

    public CompiledBodyRef BodyA { get; }
    public CompiledBodyRef BodyB { get; }
}
