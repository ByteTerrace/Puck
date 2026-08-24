using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>
/// The subject an <see cref="WorldCameraProgramOp.Anchor"/>/<see cref="WorldCameraProgramOp.LookAt"/> op resolves
/// against — the closed "what a camera program can key off" vocabulary. Distinct from <see cref="WorldAnchor"/> (WHERE
/// a whole placeable camera or speaker rides, resolved OUTSIDE the program and handed in as one reference pose): this
/// is presentation math INSIDE the program, so it stays float and needs no live entity table — a placement's pose
/// resolves through the same static stamped-transform math a placeable camera's own anchor and a speaker read
/// (<c>Puck.World.Client.WorldAnchorGeometry</c>).
/// </summary>
[System.Text.Json.Serialization.JsonDerivedType(typeof(WorldCameraSubject.Reference), typeDiscriminator: "reference")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WorldCameraSubject.Placement), typeDiscriminator: "placement")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WorldCameraSubject.WorldPoint), typeDiscriminator: "worldPoint")]
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldCameraSubject {
    private WorldCameraSubject() {
    }

    /// <summary>The program's externally supplied reference pose — a named camera's own <see cref="WorldAnchor"/>, or a
    /// seat rig's currently perceived body (the seat's own avatar, or a possessed camera body). Resolved outside the
    /// program and handed to it as the <c>SdfAnchor</c> the compiled rig receives every frame.</summary>
    public sealed record Reference : WorldCameraSubject;
    /// <summary>Rides a placement's authored stamped transform (position only — a placement carries no live facing
    /// through this static resolve, the same limitation a placeable camera's own <see cref="WorldAnchor.Placement"/>
    /// arm and a speaker share).</summary>
    /// <param name="PlacementId">The referenced <see cref="WorldPlacement.Id"/> (must resolve).</param>
    /// <param name="ShapeId">The referenced creation's own shape to ride, or <see langword="null"/> for the
    /// placement's stamped root transform.</param>
    public sealed record Placement(string PlacementId, int? ShapeId = null) : WorldCameraSubject;
    /// <summary>A fixed world-space point.</summary>
    /// <param name="Point">The world-space position.</param>
    public sealed record WorldPoint(DocumentVector3 Point) : WorldCameraSubject;
}
/// <summary>
/// One instruction in an authored camera program's ordered op list — the presentation-side pose algebra
/// <c>bodyMotionPrograms</c> established for sim-side movement, promoted to cameras: an authored rig is an ordered
/// list of these trivial ops rather than a bespoke closed motion/aim union, so a new camera behavior is authored data,
/// never new engine code. This is the AUTHORING vocabulary only: <c>Puck.World.Client.WorldCameraRigCompiler</c>
/// translates it to <c>Puck.SdfVm.Views.SdfCameraOp</c> and resolves each frame's bindings and subject poses, and
/// <c>Puck.SdfVm.Views.SdfCameraProgramEvaluator</c> — which parses no document — walks the result. Floats
/// throughout; presentation carries no fixed-point burden.
/// </summary>
[System.Text.Json.Serialization.JsonDerivedType(typeof(WorldCameraProgramOp.Anchor), typeDiscriminator: "anchor")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WorldCameraProgramOp.Offset), typeDiscriminator: "offset")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WorldCameraProgramOp.LookAt), typeDiscriminator: "lookAt")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WorldCameraProgramOp.Orbit), typeDiscriminator: "orbit")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WorldCameraProgramOp.Dynamics), typeDiscriminator: "dynamics")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WorldCameraProgramOp.ClampPitch), typeDiscriminator: "clampPitch")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WorldCameraProgramOp.Fov), typeDiscriminator: "fov")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WorldCameraProgramOp.Blend), typeDiscriminator: "blend")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WorldCameraProgramOp.Select), typeDiscriminator: "select")]
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldCameraProgramOp {
    private WorldCameraProgramOp() {
    }

    /// <summary>Gets this op's authored <c>$type</c> token — the one spelling a refusal, a read-back, and the
    /// document all use.</summary>
    public string Opcode => (this switch {
        Anchor => "anchor",
        Blend => "blend",
        ClampPitch => "clampPitch",
        Dynamics => "dynamics",
        Fov => "fov",
        LookAt => "lookAt",
        Offset => "offset",
        Orbit => "orbit",
        Select => "select",
        _ => "unknown",
    });

    /// <summary>Establishes the CURRENT subject — the pose <see cref="Offset"/>/<see cref="Orbit"/> place the eye
    /// relative to, and <see cref="LookAt"/> aims along the facing of when it names no subject of its own. Also
    /// re-seeds the eye at the resolved subject's own position (the "first person" default before any
    /// <see cref="Offset"/>/<see cref="Orbit"/> runs). At most one per program, and it must be the first operation
    /// when present; a program that omits it starts from <see cref="WorldCameraSubject.Reference"/> implicitly.</summary>
    /// <param name="Subject">The subject to resolve.</param>
    public sealed record Anchor(WorldCameraSubject Subject) : WorldCameraProgramOp;
    /// <summary>Places the eye at an offset from the current subject's pose.</summary>
    /// <param name="Value">The offset.</param>
    /// <param name="WorldAxes">Whether <paramref name="Value"/> uses world axes rather than the subject's own.</param>
    /// <param name="SpreadPullback">The group-spread multiplier applied to the offset — meaningful only when the
    /// program's OWN externally supplied reference is a <see cref="WorldAnchor.Group"/> establishing shot (zero, and
    /// thus inert, everywhere else).</param>
    public sealed record Offset(DocumentVector3 Value, bool WorldAxes = false, float SpreadPullback = 0f) : WorldCameraProgramOp;
    /// <summary>Sets the aim target.</summary>
    /// <param name="Subject">The subject to look at, or <see langword="null"/> to look along the current subject's
    /// own forward axis from the eye, at <paramref name="FocusDistance"/>.</param>
    /// <param name="TargetOffset">An offset from the resolved <paramref name="Subject"/>'s pose; ignored when
    /// <paramref name="Subject"/> is <see langword="null"/>.</param>
    /// <param name="WorldAxes">Whether <paramref name="TargetOffset"/> uses world axes rather than the subject's
    /// own.</param>
    /// <param name="FocusDistance">The finite target distance along the current subject's forward axis, used only
    /// when <paramref name="Subject"/> is <see langword="null"/>.</param>
    public sealed record LookAt(WorldCameraSubject? Subject, DocumentVector3? TargetOffset = null, bool WorldAxes = false, float FocusDistance = 6f) : WorldCameraProgramOp;
    /// <summary>Orbits the eye about its pivot. <paramref name="Yaw"/> and <paramref name="Pitch"/> are live-bindable
    /// like <see cref="Fov"/>'s field of view: a seat rig whose yaw reads a state cell turns the CAMERA when that cell
    /// changes — look behind is <c>state.look.behind</c> flipping between 0 and π — while the seat's facing (what
    /// steering and movement resolve against) is untouched, because the facing never includes the rig's offsets.</summary>
    /// <param name="Distance">The finite positive orbit distance.</param>
    /// <param name="Yaw">The orbit heading in radians, as a literal or numeric state binding.</param>
    /// <param name="Pitch">The orbit tilt in radians, as a literal or numeric state binding.</param>
    /// <param name="PivotOffset">The world-axis offset from the current subject's origin to the pivot.</param>
    public sealed record Orbit(float Distance, BindableScalar Yaw, BindableScalar Pitch, DocumentVector3? PivotOffset = null) : WorldCameraProgramOp;
    /// <summary>Sets the second-order response the resolved eye/target boom eases through. Read by the caller after
    /// resolving a frame — never affects the resolved pose itself. At most one per program.</summary>
    /// <param name="Row">The referenced <c>dynamics</c> row name; must resolve.</param>
    public sealed record Dynamics(string Row) : WorldCameraProgramOp;
    /// <summary>Clamps the effective pitch the NEXT <see cref="Orbit"/> op resolves with (its authored
    /// <see cref="Orbit.Pitch"/> plus any live seat delta) to <c>[MinPitch, MaxPitch]</c>. At most one per program;
    /// must precede the <see cref="Orbit"/> op it governs. A joined seat's own <c>views.seatControl</c> band already
    /// clamps its LIVE delta before this ever runs — this op exists for a program with no seat behind it (a named
    /// camera orbiting a state-bound pitch).</summary>
    /// <param name="MinPitch">The minimum pitch, radians.</param>
    /// <param name="MaxPitch">The maximum pitch, radians.</param>
    public sealed record ClampPitch(float MinPitch, float MaxPitch) : WorldCameraProgramOp;
    /// <summary>Sets the rendered vertical field of view, radians — a literal, or a <c>state.&lt;row&gt;[.&lt;key&gt;]</c>
    /// binding so a world rule can pull focus or frame a moment (decisions in the sim, framing in presentation). At
    /// most one per program.</summary>
    /// <param name="FieldOfViewRadians">The field of view.</param>
    public sealed record Fov(BindableScalar FieldOfViewRadians) : WorldCameraProgramOp;
    /// <summary>Evaluates two other authored programs by name and linearly interpolates their resolved eye, target,
    /// and field of view — the whole document's camera-program table (every <c>cameras[].rig</c> plus
    /// <c>views.seatRig</c>/<c>views.cameraRig</c>) is the namespace <paramref name="A"/>/<paramref name="B"/>
    /// resolve against. At most one per program; refused when it would create a reference cycle.</summary>
    /// <param name="A">The program resolved at <paramref name="Weight"/> 0.</param>
    /// <param name="B">The program resolved at <paramref name="Weight"/> 1.</param>
    /// <param name="Weight">The blend weight in <c>[0, 1]</c> — a literal, or a state binding.</param>
    public sealed record Blend(string A, string B, BindableScalar Weight) : WorldCameraProgramOp;
    /// <summary>Evaluates exactly one of several other named programs, chosen by a live key — the state-driven
    /// counterpart of <see cref="Blend"/>'s two-way continuous mix: a rule-advanced integer row selects a whole
    /// framing outright rather than easing toward one, so a document can drive which of N stations a single camera
    /// program shows without a per-frame console override. The winning program's eye, target, field of view, and
    /// dynamics response pass through unchanged (no lerp). At most one per program; refused when it would create a
    /// reference cycle.</summary>
    /// <param name="Key">The selecting value — a literal, or (the intended use) a
    /// <c>state.&lt;row&gt;[.&lt;key&gt;]</c> binding — rounded to the nearest whole number and matched against
    /// <paramref name="Cases"/>.</param>
    /// <param name="Cases">The candidate programs, keyed by <see cref="WorldCameraSelectCase.Value"/>. At most one
    /// case per value.</param>
    /// <param name="Default">The program resolved when no case's value matches <paramref name="Key"/>.</param>
    public sealed record Select(BindableScalar Key, IReadOnlyList<WorldCameraSelectCase> Cases, string Default) : WorldCameraProgramOp;
}
/// <summary>One <see cref="WorldCameraProgramOp.Select"/> candidate: the program named by <see cref="Program"/> wins
/// when the op's key rounds to <see cref="Value"/>.</summary>
/// <param name="Value">The matching key value.</param>
/// <param name="Program">The candidate program's name — the same blend namespace <see cref="WorldCameraProgramOp.Blend"/>
/// resolves against (every <c>cameras[].rig</c>, plus <c>views.seatRig</c>/<c>views.cameraRig</c>).</param>
public sealed record WorldCameraSelectCase(long Value, string Program);
/// <summary>An authored camera program — the ordered op list a rig resolves through every frame.</summary>
/// <param name="Name">The stable name a <see cref="WorldCameraProgramOp.Blend"/> op selects this program by.</param>
/// <param name="Version">The instruction-set version.</param>
/// <param name="Operations">The selected ops, in authored evaluation order.</param>
public sealed record WorldCameraProgram(string Name, string Version, IReadOnlyList<WorldCameraProgramOp> Operations) {
    /// <summary>The supported camera-program instruction-set version.</summary>
    public const string CurrentVersion = "puck.camera.v1";
    /// <summary>The largest admitted operation count.</summary>
    public const int MaxOperations = 16;

    /// <summary>Gets the program's <see cref="WorldCameraProgramOp.Anchor"/> op, or <see langword="null"/> when absent
    /// (an absent anchor starts from <see cref="WorldCameraSubject.Reference"/> implicitly).</summary>
    public WorldCameraProgramOp.Anchor? AnchorOp => FirstOrDefault<WorldCameraProgramOp.Anchor>();
    /// <summary>Gets the program's <see cref="WorldCameraProgramOp.Blend"/> op, or <see langword="null"/>.</summary>
    public WorldCameraProgramOp.Blend? BlendOp => FirstOrDefault<WorldCameraProgramOp.Blend>();
    /// <summary>Gets the program's <see cref="WorldCameraProgramOp.ClampPitch"/> op, or <see langword="null"/>.</summary>
    public WorldCameraProgramOp.ClampPitch? ClampPitchOp => FirstOrDefault<WorldCameraProgramOp.ClampPitch>();
    /// <summary>Gets the program's <see cref="WorldCameraProgramOp.Fov"/> op, or <see langword="null"/>.</summary>
    public WorldCameraProgramOp.Fov? FovOp => FirstOrDefault<WorldCameraProgramOp.Fov>();
    /// <summary>Gets the program's <see cref="WorldCameraProgramOp.LookAt"/> op, or <see langword="null"/>.</summary>
    public WorldCameraProgramOp.LookAt? LookAtOp => FirstOrDefault<WorldCameraProgramOp.LookAt>();
    /// <summary>Gets the program's <see cref="WorldCameraProgramOp.Offset"/> op, or <see langword="null"/>.</summary>
    public WorldCameraProgramOp.Offset? OffsetOp => FirstOrDefault<WorldCameraProgramOp.Offset>();
    /// <summary>Gets the program's <see cref="WorldCameraProgramOp.Orbit"/> op, or <see langword="null"/>.</summary>
    public WorldCameraProgramOp.Orbit? OrbitOp => FirstOrDefault<WorldCameraProgramOp.Orbit>();
    /// <summary>Gets the program's <see cref="WorldCameraProgramOp.Dynamics"/> op, or <see langword="null"/>.</summary>
    public WorldCameraProgramOp.Dynamics? DynamicsOp => FirstOrDefault<WorldCameraProgramOp.Dynamics>();
    /// <summary>Gets the program's <see cref="WorldCameraProgramOp.Select"/> op, or <see langword="null"/>.</summary>
    public WorldCameraProgramOp.Select? SelectOp => FirstOrDefault<WorldCameraProgramOp.Select>();

    private TOp? FirstOrDefault<TOp>() where TOp : WorldCameraProgramOp {
        var operations = Operations;

        if (operations is null) {
            return null;
        }

        for (var index = 0; (index < operations.Count); index++) {
            if (operations[index] is TOp match) {
                return match;
            }
        }

        return null;
    }
}
