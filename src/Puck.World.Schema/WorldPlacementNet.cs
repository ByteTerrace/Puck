using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics;

namespace Puck.World;

/// <summary>
/// A placement's net facet: the row's creation is a net of rope strands that gives under the bodies that lean on it and
/// carries the bodies that hold it (a climbing net, a rope bridge, a hammock, a cargo net, a banner, a sail). The
/// authority keeps a coarse grid of nodes over the parallelogram <c>Origin + s·Across + t·Up</c> in the row's frame,
/// steps it in fixed point every tick it is awake (<c>Puck.Physics.FixedNet</c>: its strands' caps, its pins, its
/// reach), and lets it sleep when nothing touches it. The net collides as a sheet of thin boxes over its node cells,
/// posed every tick, a body holding it rides it and loads it with its weight, and a body leaning on it pushes its nodes
/// out; presentation draws the creation baked once at rest and displaces each vertex by the nodes' offsets about it, so
/// a net that moves rebuilds no program and keys no mesh.
/// </summary>
/// <remarks>The facet's frame is the row's resolved frame: <see cref="Origin"/>, <see cref="Across"/> and
/// <see cref="Up"/> are authored in the creation's units, turned by the row's yaw and scaled with its scale, so a net
/// authored over a creation's frame stays on it wherever the row stands. A net row has one static frame: no
/// distribution, mirror, attach, turn or inhabit (<see cref="WorldDefinitionValidator"/>).</remarks>
/// <param name="Columns">The nodes along <paramref name="Across"/>, 2 to <see cref="MaxNodesPerEdge"/>.</param>
/// <param name="Rows">The nodes along <paramref name="Up"/>, 2 to <see cref="MaxNodesPerEdge"/>.</param>
/// <param name="Origin">The rest position of node (0, 0), in the row's frame.</param>
/// <param name="Across">The rest edge from node (0, 0) to the last column's, in the row's frame.</param>
/// <param name="Up">The rest edge from node (0, 0) to the last row's, in the row's frame; not parallel to
/// <paramref name="Across"/>.</param>
/// <param name="PinLeft">Whether the first column, along <paramref name="Up"/> at the origin, is held to the frame.</param>
/// <param name="PinRight">Whether the last column is held to the frame.</param>
/// <param name="PinBottom">Whether the first row, along <paramref name="Across"/> at the origin, is held to the frame.</param>
/// <param name="PinTop">Whether the last row is held to the frame. At least one edge is held.</param>
/// <param name="NodeMass">The mass one free node carries, in kilograms; positive.</param>
/// <param name="Stiffness">One strand's stiffness against stretch past its length, in newtons per metre; positive.</param>
/// <param name="Damping">The share of a node's speed lost each second; non-negative.</param>
/// <param name="Sag">The slack each strand carries past its rest spacing, as a share of it, in <c>[−0.5, 1]</c>: positive
/// hangs the net loose, so it gives freely until its strands draw taut; negative strings it taut, each strand that share
/// shorter than the spacing, so a load bellies it in proportion to its weight.</param>
/// <param name="Reach">The farthest a node moves from rest, in metres before the row's scale; positive.</param>
/// <param name="Substeps">The slices a step is cut into, each solving every strand once, 1 to <see cref="MaxSubsteps"/>:
/// more converge a stiffer net, at a strand solve each per strand.</param>
/// <param name="Strand">The thickness the net collides at, in metres before the row's scale: each node cell is a box
/// this thick about the nodes' surface, and a body leaning on the net pushes its nodes until they stand this far beyond
/// the box's face; positive.</param>
/// <param name="BodyMass">The mass, in kilograms, a body of authored size loads the net with while it holds it, scaled by
/// the cube of the body's size; non-negative.</param>
/// <param name="RestSpeed">The speed, in metres a second, under which every node counts as still; non-negative.</param>
/// <param name="SleepSeconds">The seconds every node must stay still, untouched, before the net sleeps; non-negative.</param>
/// <param name="Gravity">The acceleration every free node falls at along world down, in metres a second squared;
/// non-negative.</param>
public sealed record WorldPlacementNet(
    int Columns,
    int Rows,
    DocumentVector3 Origin,
    DocumentVector3 Across,
    DocumentVector3 Up,
    bool PinLeft = true,
    bool PinRight = true,
    bool PinBottom = true,
    bool PinTop = true,
    float NodeMass = 0.2f,
    float Stiffness = 20000f,
    float Damping = 3f,
    float Sag = 0f,
    float Reach = 0.3f,
    int Substeps = 8,
    float Strand = 0.035f,
    float BodyMass = 60f,
    float RestSpeed = 0.01f,
    float SleepSeconds = 1f,
    float Gravity = 9.81f
) {
    /// <summary>The most nodes a net takes along either edge.</summary>
    public const int MaxNodesPerEdge = 16;
    /// <summary>The most slices a step is cut into.</summary>
    public const int MaxSubsteps = 32;

    /// <summary>Gets the edges the facet pins.</summary>
    [JsonIgnore]
    public FixedNetPins PinMask =>
        ((PinLeft ? FixedNetPins.Left : FixedNetPins.None) | (PinRight ? FixedNetPins.Right : FixedNetPins.None) |
        (PinBottom ? FixedNetPins.Bottom : FixedNetPins.None) | (PinTop ? FixedNetPins.Top : FixedNetPins.None));

    /// <summary>Returns the net's layout and material in world coordinates where a row's resolved frame stands it: the
    /// facet's frame turned by the row's rotation, scaled by its scale and moved to its origin, with the reach and
    /// strand scaled alike.</summary>
    /// <param name="origin">The row's resolved origin.</param>
    /// <param name="rotation">The row's resolved rotation.</param>
    /// <param name="scale">The row's scale.</param>
    /// <returns>The kernel's spec.</returns>
    public FixedNetSpec SpecAt(in FixedVector3 origin, in FixedQuaternion rotation, FixedQ4816 scale) => new(
        Across: rotation.Rotate(vector: (FixedVector3.FromVector3(value: Across) * scale)),
        Columns: Columns,
        Damping: FixedQ4816.FromDouble(value: Damping),
        Substeps: Substeps,
        NodeMass: FixedQ4816.FromDouble(value: NodeMass),
        Origin: (origin + rotation.Rotate(vector: (FixedVector3.FromVector3(value: Origin) * scale))),
        Pins: PinMask,
        Reach: (FixedQ4816.FromDouble(value: Reach) * scale),
        RestSpeed: FixedQ4816.FromDouble(value: RestSpeed),
        Rows: Rows,
        Sag: FixedQ4816.FromDouble(value: Sag),
        SleepTicks: FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: SleepSeconds)),
        Stiffness: FixedQ4816.FromDouble(value: Stiffness),
        Up: rotation.Rotate(vector: (FixedVector3.FromVector3(value: Up) * scale))
    );
    /// <summary>Returns the net's layout where a placement stands in a document: the row's one resolved stamp instance
    /// and rotation (<c>WorldPlacementStamp.Resolve</c>) and its scale.
    /// Both the authority's kernel and presentation's displacement take the frame here, so the two never disagree on
    /// where a node rests.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="placement">The row carrying the facet.</param>
    /// <param name="spec">The spec, when the row resolves to exactly one stamp instance.</param>
    /// <returns>Whether the row resolved to exactly one instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="placement"/> is
    /// <see langword="null"/>.</exception>
    public bool TrySpecIn(WorldDefinition definition, WorldPlacement placement, out FixedNetSpec spec) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: placement);

        var stamp = WorldPlacementStamp.Resolve(definition, placement, worldSeed: (definition.Generation?.WorldSeed ?? 0UL));

        if (stamp.Count != 1) {
            spec = default;
            return false;
        }
        spec = SpecAt(origin: stamp.Instances[0].Origin, rotation: stamp.FixedRotation, scale: FixedQ4816.FromDouble(value: placement.Scale));
        return true;
    }
    /// <summary>Checks the facet, adding one line to <paramref name="errors"/> for each refusal: a node count outside
    /// <c>[2, MaxNodesPerEdge]</c>, edges that are zero or parallel, no pinned edge, a mass, stiffness,
    /// reach or strand that is not positive and finite, a damping, rest speed, sleep time or body mass that is negative or
    /// not finite, a sag outside <c>[−0.5, 1]</c>, or a substep count outside <c>[1, MaxSubsteps]</c>.</summary>
    /// <param name="path">The facet's document path, which every refusal starts with.</param>
    /// <param name="errors">The refusals.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="errors"/> is
    /// <see langword="null"/>.</exception>
    public void Validate(string path, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: path);
        ArgumentNullException.ThrowIfNull(argument: errors);

        if ((Columns < 2) || (Columns > MaxNodesPerEdge) || (Rows < 2) || (Rows > MaxNodesPerEdge)) {
            errors.Add(item: $"{path}: a net takes 2 to {MaxNodesPerEdge} nodes along each edge, not {Columns} by {Rows}");
        }
        var across = Across.Value;
        var up = Up.Value;

        if (!float.IsFinite(f: across.LengthSquared()) || !float.IsFinite(f: up.LengthSquared()) || !float.IsFinite(f: Origin.Value.LengthSquared()) ||
            (System.Numerics.Vector3.Cross(vector1: across, vector2: up).LengthSquared() <= 1e-12f)) {
            errors.Add(item: $"{path}: a net's across and up edges are finite, nonzero and not parallel");
        }
        if (PinMask == FixedNetPins.None) { errors.Add(item: $"{path}: a net holds at least one edge to its frame (pinLeft, pinRight, pinBottom, pinTop)"); }
        Positive(value: NodeMass, name: "nodeMass");
        Positive(value: Stiffness, name: "stiffness");
        Positive(value: Reach, name: "reach");
        Positive(value: Strand, name: "strand");
        NonNegative(value: Damping, name: "damping");
        NonNegative(value: RestSpeed, name: "restSpeed");
        NonNegative(value: SleepSeconds, name: "sleepSeconds");
        NonNegative(value: BodyMass, name: "bodyMass");
        NonNegative(value: Gravity, name: "gravity");
        if (!float.IsFinite(f: Sag) || (Sag < -0.5f) || (Sag > 1f)) { errors.Add(item: $"{path}.sag: a net's sag lies in [-0.5, 1], not {Sag}"); }
        if ((Substeps < 1) || (Substeps > MaxSubsteps)) { errors.Add(item: $"{path}.substeps: a net takes 1 to {MaxSubsteps} substeps, not {Substeps}"); }

        void Positive(float value, string name) {
            if (!float.IsFinite(f: value) || (value <= 0f)) { errors.Add(item: $"{path}.{name}: must be positive and finite, not {value}"); }
        }
        void NonNegative(float value, string name) {
            if (!float.IsFinite(f: value) || (value < 0f)) { errors.Add(item: $"{path}.{name}: must be non-negative and finite, not {value}"); }
        }
    }
}
