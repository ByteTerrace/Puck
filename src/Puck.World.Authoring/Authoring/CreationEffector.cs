using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.World.Authoring;

/// <summary>
/// Where an effector's tip is asked to be — the goal half of an <see cref="CreationEffectorDocument"/>, kept as its own
/// record so a target kind can be added without touching the chain grammar. Nothing here names a limb, a surface type,
/// or a creature: the probe direction is body-relative and the field answers whatever geometry is there, so a hand
/// reaching for a wall, a foot for ground, and a tarsus for a ceiling are the same three numbers.
/// </summary>
/// <param name="Kind">One of <see cref="KindSurface"/>, <see cref="KindBody"/>, or <see cref="KindState"/>.</param>
/// <param name="Direction"><see cref="KindSurface"/>: the probe direction in the creation's author frame (see
/// <see cref="CreationFrame"/>), so it turns with the body — <c>[0, -1, 0]</c> is "below the body", which on a wall-held
/// body points at the wall's own down. Normalized at canonicalization; zero names no direction and is refused.</param>
/// <param name="Reach"><see cref="KindSurface"/>: how far along <see cref="Direction"/> the probe searches, world
/// units. Beyond <see cref="MaxReach"/>, or at zero or less, is refused. A probe that finds nothing eases the
/// correction out rather than reaching at nothing.</param>
/// <param name="Standoff"><see cref="KindSurface"/>: how far off the hit surface, along its normal, the tip is placed
/// (null = 0 — the tip lands on the surface). The thickness of a boot's sole or the gap a claw keeps.</param>
/// <param name="Index"><see cref="KindBody"/>: the population entity index whose root pose is the target.</param>
/// <param name="Offset"><see cref="KindBody"/>: the offset from that body's root, in the creation's author frame
/// (null = its root exactly).</param>
/// <param name="Reference"><see cref="KindState"/>: a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> reference to a text cell
/// spelling a world-space <c>[x, y, z]</c>, read at the frame's tick — a target a rule, a console write, or another
/// system publishes. The containing world refuses a row it does not declare.</param>
public sealed record CreationEffectorTargetDocument(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Direction = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentScalar? Reach = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentScalar? Standoff = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Index = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Offset = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reference = null
) {
    /// <summary>Another body's root pose, offset by <see cref="Offset"/> — a hand on a carried crate, a mouth on a
    /// prey body, a tow arm on a hauled kart.</summary>
    public const string KindBody = "body";
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
    public static bool IsKind(string? kind) => (kind is (KindSurface or KindBody or KindState));
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
public sealed record CreationPlantDocument(string Driver, DocumentVector2 Window) {
    /// <summary>One full turn — the exclusive bound both window ends sit under.</summary>
    public const float TwoPi = (2f * MathF.PI);
}
/// <summary>
/// One inverse-kinematics effector a creation declares: a chain of its own shapes, root→tip, whose driver-posed pose is
/// corrected so the tip reaches a target. The solve composes after the drivers and the parent chain and bends the pose
/// they produced; it never starts from a rest or default pose.
/// <para>Presentation-only, on exactly the terms <see cref="ShapeSwingDocument"/> is: the correction composes onto the
/// per-frame dynamic transforms in <c>Puck.World.Client.WorldStampPool</c> and nothing else reads it, so the emitted
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
/// gate's eased weight, so a half-weight effector suggests rather than commands.</param>
/// <param name="Plant">The contact latch (null = none) — see <see cref="CreationPlantDocument"/>.</param>
public sealed record CreationEffectorDocument(
    string Name,
    IReadOnlyList<string> Chain,
    string Tip,
    CreationEffectorTargetDocument Target,
    [property: JsonConverter(typeof(DriverGateJsonConverter)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? When = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentScalar? Weight = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CreationPlantDocument? Plant = null
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
