using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.World.Authoring;

/// <summary>
/// Where an effector's tip is asked to be — the goal half of an <see cref="CreationEffectorDocument"/>, kept as its own
/// record so a target kind can be added without touching the chain grammar. Nothing here names a limb, a surface type,
/// or a creature: the probe direction is body-relative and the field answers whatever geometry is there, so a hand
/// reaching for a wall, a foot for ground, and a tarsus for a ceiling are the same three numbers.
/// </summary>
/// <param name="Kind">One of <see cref="KindSurface"/>, <see cref="KindBody"/>, <see cref="KindState"/> or
/// <see cref="KindShape"/>.</param>
/// <param name="Direction"><see cref="KindSurface"/>: the probe direction in the creation's author frame (see
/// <see cref="CreationFrame"/>), so it turns with the body — <c>[0, -1, 0]</c> is "below the body", which on a wall-held
/// body points at the wall's own down. <see cref="KindShape"/> (optional): the way the tip approaches the point, carried
/// by <see cref="Shape"/>'s motion; its reverse is the normal an effector's align turns the tip onto. Normalized at
/// canonicalization; zero names no direction and is refused.</param>
/// <param name="Reach"><see cref="KindSurface"/>: how far along <see cref="Direction"/> the probe searches, in creation
/// units: world units at the body's scale of one, and multiplied by its scale, as its shapes are. Beyond
/// <see cref="MaxReach"/>, or at zero or less, is refused. A probe that finds nothing eases the correction out rather
/// than reaching at nothing.</param>
/// <param name="Standoff"><see cref="KindSurface"/>: how far off the hit surface, along its normal, the tip is placed,
/// in creation units as <paramref name="Reach"/> is (null = 0 — the tip lands on the surface). The thickness of a
/// boot's sole or the gap a claw keeps.</param>
/// <param name="Index"><see cref="KindBody"/>: the population entity index whose pose is the target, fixed in the
/// document. A body target names this or <paramref name="Reference"/>, never both.</param>
/// <param name="Offset"><see cref="KindBody"/>: the offset from that body's root (or its <see cref="Part"/>), in the
/// creation's author frame, riding the target's attitude (null = the point exactly). <see cref="KindShape"/>: a point
/// in the creation at rest, author frame, that rides <see cref="Shape"/>'s motion (null = the shape's own
/// origin).</param>
/// <param name="Reference"><see cref="KindState"/>: a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> reference to a text cell
/// spelling a world-space <c>[x, y, z]</c>, read at the frame's tick — a target a rule, a console write, or another
/// system publishes. <see cref="KindBody"/>: a reference to a numeric cell holding the target body's population index,
/// read as stored truth each frame: a whole number naming an active body selects it, and any other value or none
/// selects none, so a rule clears the target by clearing the cell or writing -1. The containing world refuses a row it
/// does not declare.</param>
/// <param name="Shape"><see cref="KindShape"/>: the name of a shape of the same creation the point rides. It may not be
/// a bone or the tip of the effector's own chain, nor hang from one, and no effector declared later may write it or a
/// shape it hangs from, so the solve reads the shape's final pose for the frame.</param>
/// <param name="Part"><see cref="KindBody"/> (optional): a part id the target body's look publishes; the point is that
/// part's anchor as last drawn, and the body's root when its look publishes no such part.</param>
public sealed record CreationEffectorTargetDocument(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Direction = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentScalar? Reach = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentScalar? Standoff = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Index = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Offset = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reference = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Shape = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Part = null
) {
    /// <summary>Another body's root or part pose, offset by <see cref="Offset"/> — a hand on a carried crate, a mouth on
    /// a prey body, a tow arm on a hauled kart, a hand on the shoulder of a body a rule chose.</summary>
    public const string KindBody = "body";
    /// <summary>A point riding a shape of the reaching creation itself, read in the same frame after every driver and
    /// every earlier effector: a fingertip on a vent of the instrument the other arm holds, a hand on a hip.</summary>
    public const string KindShape = "shape";
    /// <summary>A world-space point published in a state cell.</summary>
    public const string KindState = "state";
    /// <summary>The field surface the probe finds along <see cref="Direction"/> within <see cref="Reach"/>.</summary>
    public const string KindSurface = "surface";
    /// <summary>The largest <see cref="Offset"/> magnitude, world units — past a body's own envelope the offset names
    /// a point unrelated to the body it is anchored to.</summary>
    public const float MaxOffset = 16f;
    /// <summary>The largest <see cref="Reach"/>, world units. A limb probe is a local question; a longer march is a
    /// per-frame cost with no rig that needs it.</summary>
    public const float MaxReach = 8f;
    /// <summary>The largest <see cref="Standoff"/>, world units.</summary>
    public const float MaxStandoff = 1f;

    /// <summary>Returns whether a target kind name is one this engine resolves.</summary>
    /// <param name="kind">The kind name.</param>
    public static bool IsKind(string? kind) => (kind is (KindSurface or KindBody or KindState or KindShape));
}
/// <summary>
/// The contact latch: while the named driver's phase is inside <see cref="Window"/>, the effector's world target is
/// held where it was when the window opened, so the tip stays put in the world while the body travels through it. A
/// quadruped's stance phase, a climber's hand on a hold, and a tentacle tip gripping while the trunk sways are the
/// same mechanism with different windows.
/// </summary>
/// <param name="Driver">The <see cref="CreationDriverDocument.Name"/> whose wrapped phase the window is read
/// against.</param>
/// <param name="Window">The phase interval <c>[from, to]</c> in radians, each in <c>[0, 2π)</c>. A window whose
/// <c>from</c> exceeds its <c>to</c> wraps through 0 — the half-cycle straddling the phase origin is as authorable as
/// any other.</param>
/// <param name="SwingWeight">The target influence outside the plant window while the driver is active, in [0, 1].
/// Null means 1 (continue following the target); 0 releases the tip to its authored swing. As the driver eases to
/// rest, target influence returns to 1 so an idle limb can settle onto its surface.</param>
/// <param name="Replant">The stepping latch the tip keeps while <paramref name="Driver"/> is at rest (null = none,
/// and a resting tip follows its target every frame) — see <see cref="CreationReplantDocument"/>.</param>
public sealed record CreationPlantDocument(
    string Driver,
    DocumentVector2 Window,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? SwingWeight = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CreationReplantDocument? Replant = null
) {
    /// <summary>One full turn — the exclusive bound both window ends sit under.</summary>
    public const float TwoPi = (2f * MathF.PI);
}
/// <summary>
/// The resting latch of a <see cref="CreationPlantDocument"/>: while the plant's driver rests, the tip holds the
/// world point it stood on, and once the point its target resolves to this frame lies more than
/// <see cref="Distance"/> from that hold, it steps there, travelling over <see cref="Seconds"/> along an arc that
/// rises <see cref="Height"/> against the target's probe direction. Effectors whose plants name the same driver step
/// one at a time, so a pair of feet turning in place, or settling into a stance after a stop, takes alternate steps
/// instead of sliding. Presentation-only, as every effector is.
/// </summary>
/// <param name="Distance">The drift that starts a step, in creation units (world units at the body's scale of one,
/// multiplied by its scale), in (0, <see cref="MaxDistance"/>].</param>
/// <param name="Seconds">The step's duration, seconds, in (0, <see cref="MaxSeconds"/>].</param>
/// <param name="Height">The arc's peak lift, in creation units as <paramref name="Distance"/> is, in
/// [0, <see cref="MaxHeight"/>].</param>
public sealed record CreationReplantDocument(float Distance, float Seconds, float Height) {
    /// <summary>The largest <see cref="Distance"/>, world units.</summary>
    public const float MaxDistance = 2f;
    /// <summary>The largest <see cref="Height"/>, world units.</summary>
    public const float MaxHeight = 1f;
    /// <summary>The largest <see cref="Seconds"/>.</summary>
    public const float MaxSeconds = 2f;
}
/// <summary>
/// One inverse-kinematics effector a creation declares: a chain of its own shapes, root→tip, whose driver-posed pose is
/// corrected so the tip reaches a target. The solve composes after the drivers and the parent chain and bends the pose
/// they produced; it never starts from a rest or default pose.
/// <para>Presentation-only, on exactly the terms <see cref="ShapeSwingDocument"/> is: the correction composes onto the
/// per-frame dynamic transforms in <c>Puck.World.Client.WorldCreationInstances</c> and nothing else reads it, so the emitted
/// SDF program, the analytic colliders, the compiled solid field, and every simulation value are blind to it.</para>
/// </summary>
/// <param name="Name">The effector's name, unique within the creation — what a read-back or a refusal spells.</param>
/// <param name="Chain">The bones, root→tip, each the <see cref="ShapeDocument.Name"/> of a shape that DESCENDS from the
/// one before it through <see cref="ShapeDocument.Parent"/>. A bone's joint is the pivot of its first
/// <see cref="ShapeDocument.Swings"/> entry, or its authored <see cref="ShapeDocument.Joint"/> when it swings nothing.
/// Two bones (<see cref="MinChainBones"/>) solve analytically; three or more — a tail, a tentacle, a spider leg with a
/// coxa — solve by cyclic coordinate descent over <see cref="Iterations"/> sweeps. At most
/// <see cref="MaxChainBones"/>.</param>
/// <param name="Tip">The <see cref="ShapeDocument.Name"/> of the shape whose posed origin IS the end effector — the
/// last bone itself, or a shape descending from it (a boot under a shin, a claw under a tarsus).</param>
/// <param name="Target">Where the tip is asked to be.</param>
/// <param name="When">The gate, in the same vocabulary and with the same easing a driver's
/// <see cref="CreationDriverDocument.When"/> uses: the correction blends in while every token holds and back out
/// otherwise, so a released effector returns the limb to its driver-posed pose instead of dropping it.</param>
/// <param name="Weight">A constant ceiling on the correction, in [0, 1] (null = 1 — full correction). Multiplied by the
/// eased weight and the layer share, so a half-weight effector suggests rather than commands.</param>
/// <param name="Plant">The contact latch (null = none) — see <see cref="CreationPlantDocument"/>.</param>
/// <param name="Align">How much of the tip's tilt the contact takes out, in [0, 1] (null = 0: the tip keeps whatever
/// orientation the solved chain hands it). At 1 the tip's rest orientation is re-aligned to the contact normal: the
/// tip axis that points against the target's probe <see cref="CreationEffectorTargetDocument.Direction"/> at rest
/// (creation up for a target that names none) turns onto the surface normal at the target, latched with the plant, or
/// onto world up when the target reports no surface. The turn is the shortest one, about the tip's own origin, so the
/// tip stays on its target and keeps the heading the chain gave it. It is weighted by the eased weight the goal blends
/// by, so a tip a plant releases to its swing (<see cref="CreationPlantDocument.SwingWeight"/> 0) keeps its authored
/// swing off the ground. A boot whose sole is flat at rest keeps it flat when the chain bends to reach a target off its
/// rest offset, and a <see cref="CreationEffectorTargetDocument.Standoff"/> equal to the tip origin's rest height then
/// puts the whole sole on the surface.</param>
/// <param name="WeightBy">The level the effector's weight eases toward while its gate holds, from a curve of a driver's
/// phase clamped to [0, 1], read as a driver's <see cref="CreationDriverDocument.WeightBy"/> reads one (null = 1). The
/// driver it names owns the effector: the effector's layer share is that driver's share over every shape the effector
/// writes (<see cref="CreationRig.EffectorShare"/>), and a driver with no <see cref="CreationDriverDocument.Parts"/>
/// covers what its effectors write. Any declared driver may own one, since every driver advances before any effector
/// solves.</param>
/// <param name="BlendInSeconds">The exponential time constant the weight rises over, as a driver's
/// (null = <see cref="CreationDriverDocument.WeightSeconds"/>; zero is immediate).</param>
/// <param name="BlendOutSeconds">The exponential time constant the weight falls over, on the same terms.</param>
public sealed record CreationEffectorDocument(
    string Name,
    IReadOnlyList<string> Chain,
    string Tip,
    CreationEffectorTargetDocument Target,
    [property: JsonConverter(typeof(DriverGateJsonConverter)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? When = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentScalar? Weight = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CreationPlantDocument? Plant = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Align = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CreationDriverCurveDocument? WeightBy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? BlendInSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? BlendOutSeconds = null
) {
    /// <summary>The cyclic-coordinate-descent sweep CEILING for a chain of three or more bones. The sweep stops early
    /// once the tip is within <c>Puck.World.Client.WorldEffectorSolver.ReachedTolerance</c>, so this bounds the worst
    /// case rather than pricing every frame: a straight four-bone chain reaching a target off its own axis is the
    /// slowest start the method has, and it closes to a hundredth of a millimetre in 64.</summary>
    public const int Iterations = 64;
    /// <summary>The most bones a chain carries — a spider's coxa/femur/patella/tibia/metatarsus/tarsus is six, and
    /// eight leaves room for a tail without the per-bone sweep becoming a frame cost.</summary>
    public const int MaxChainBones = 8;
    /// <summary>The fewest bones a chain carries. One bone has no joint to bend at: the tip's position is whatever the
    /// bone's own drivers put it at, and an effector over it could only rotate the whole limb, which a swing already
    /// says better.</summary>
    public const int MinChainBones = 2;
}
