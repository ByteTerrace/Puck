using Puck.Commands;
using Puck.Physics.Motion;
using Puck.Maths;

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
/// <summary>What a <see cref="WorldRuleFacts.PointerPrefix"/> read reports of a seat's mapped pointer ray.</summary>
public enum PointerFacet : byte {
    /// <summary>The source-normalized horizontal fraction, <c>x</c> right, in <c>[0, 1)</c> while the ray is on the source.</summary>
    X,
    /// <summary>The source-normalized vertical fraction, <c>y</c> down, in <c>[0, 1)</c> while the ray is on the source.</summary>
    Y,
    /// <summary><c>1</c> while the ray lands on the source, <c>0</c> otherwise.</summary>
    On,
    /// <summary>The pointing participant's value of one channel while its ray lands on the source, <c>0</c> otherwise.</summary>
    Press,
}
/// <summary>A seat's pointer ray mapped through one <c>Simulation</c> screen (<see cref="WorldRuleFacts.PointerPrefix"/>).
/// The mapping is built from the screen row alone when the rule compiles, so every read maps in fixed point from
/// document data.</summary>
public sealed class PointerOperand : WorldOperandFact {
    /// <summary>The <see cref="Seat"/> value that reads every participant allowed to point at the screen: the first
    /// local seat, then the first live session by ordinal, that holds <c>Control</c> over the screen and whose ray lands
    /// on its source.</summary>
    public const int AnyParticipant = -1;

    /// <summary>Initializes a new instance of the <see cref="PointerOperand"/> class.</summary>
    /// <param name="seat">The zero-based seat, or <see cref="AnyParticipant"/>.</param>
    /// <param name="mapping">The screen row's source-normalized mapping (<see cref="WorldScreenMappings.Normalized"/>),
    /// already validated.</param>
    /// <param name="facet">The facet the read reports.</param>
    /// <param name="screenIndex">The screen the ray maps through, which a participant must hold <c>Control</c> over.</param>
    /// <param name="pressChannel">The channel ordinal a <see cref="PointerFacet.Press"/> read reports, or <c>-1</c>.</param>
    public PointerOperand(int seat, SourceMapping mapping, PointerFacet facet, int screenIndex, int pressChannel = -1) : base(((facet == PointerFacet.On)
        ? CellKind.Int
        : CellKind.Fixed)) {
        Seat = seat;
        Mapping = mapping;
        Facet = facet;
        ScreenIndex = screenIndex;
        PressChannel = pressChannel;
    }

    /// <summary>Gets the channel ordinal a <see cref="PointerFacet.Press"/> read reports, or <c>-1</c>.</summary>
    public int PressChannel { get; }
    /// <summary>Gets the screen the ray maps through.</summary>
    public int ScreenIndex { get; }

    /// <summary>Determines whether a ray lands on the screen's source.</summary>
    /// <param name="ray">The ray, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> when the ray maps onto the source.</returns>
    public bool Lands(SourceRay? ray) => (
        (ray is { } pointer) &&
        Mapping.MapRay(ray: pointer).IsOnSource
    );

    /// <summary>Gets the facet the read reports.</summary>
    public PointerFacet Facet { get; }
    /// <summary>Gets the screen row's source-normalized mapping.</summary>
    public SourceMapping Mapping { get; }
    /// <summary>Gets the zero-based seat, or <see cref="AnyParticipant"/>.</summary>
    public int Seat { get; }

    /// <summary>Returns the fact a mapped hit reads as: the facet's value while the hit lies on the source, and zero
    /// otherwise, including for no ray at all.</summary>
    /// <param name="ray">The participant's pointer ray this tick, or <see langword="null"/> for none.</param>
    /// <param name="press">The participant's value of <see cref="PressChannel"/>, read for a
    /// <see cref="PointerFacet.Press"/> facet.</param>
    /// <returns>The raw value, in <see cref="WorldOperandFact.ValueKind"/>'s encoding.</returns>
    public long Read(SourceRay? ray, FixedQ4816 press = default) {
        if (ray is not { } pointer) {
            return 0L;
        }

        var hit = Mapping.MapRay(ray: pointer);

        if (!hit.IsOnSource) {
            return 0L;
        }

        return Facet switch {
            PointerFacet.X => hit.Coordinate.X.Value,
            PointerFacet.Y => hit.Coordinate.Y.Value,
            PointerFacet.Press => press.Value,
            _ => 1L,
        };
    }
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
