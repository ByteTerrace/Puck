using System.Numerics;
using Puck.SdfVm;

namespace Puck.Demo.SdfDebug;

/// <summary>Which debuggable SDF primitive the fullscreen debug subject is. The 2D-primitive family
/// (<see cref="RoundedRect"/>..<see cref="Ellipse"/>) is lifted into 3D by the scene's shared <see cref="SdfLift"/>
/// mode/amount (see <c>sdf.lift</c>); every other kind ignores the lift.</summary>
public enum SdfDebugShapeKind {
    Sphere,
    Box,
    Torus,
    Capsule,
    Cylinder,
    Ellipsoid,
    Vesica,
    RoundCone,
    RoundedRect,
    Polygon,
    Star,
    Trapezoid,
    Ellipse,
}

/// <summary>A modifier op pushed onto the debug subject's stack. POINT-class ops (folds/warps/transforms) apply BEFORE
/// the primitive; FIELD-class ops (shell/inflate/relief) apply AFTER it (they act on the accumulated field) — see
/// <see cref="SdfDebugScene.IsFieldOp"/>. One record covers every op; each kind reads only the fields it needs.</summary>
public readonly record struct SdfDebugOp(SdfDebugOpKind Kind, Vector3 V0, Vector3 V1, float A, float B, int I0, int I1, bool Flag);

/// <summary>One runtime carve — a sphere the emitter SUBTRACTS from the assembled subject+floor field (a hard
/// <see cref="SdfBlendOp.Subtraction"/>, or a <see cref="SdfBlendOp.SmoothSubtraction"/> with <see cref="SmoothK"/>
/// when <see cref="Smooth"/>). Carves are pure DATA: a console <c>sdf.carve</c> and a pad-chord carve append the same
/// record, and the frame source rebuilds the packed program on the <see cref="SdfDebugScene.Revision"/> bump — so a
/// scripted carve sequence IS its own deterministic replay (there is no dynamic-write path; the rebuild is the model).</summary>
public readonly record struct SdfCarve(Vector3 Center, float Radius, bool Smooth, float SmoothK);

/// <summary>The op kinds the debug stack supports (a superset spanning the point-fold and field families of
/// <see cref="SdfProgramBuilder"/>). The wire order is irrelevant; <see cref="SdfDebugScene.IsFieldOp"/> classifies
/// each into its emission slot.</summary>
public enum SdfDebugOpKind {
    Twist,
    BendX,
    BendY,
    BendZ,
    Scale,
    Elongate,
    Repeat,
    RepeatLimited,
    Polar,
    Symmetry,
    LogSphere,
    CellJitter,
    DomainWarp,
    // Field ops (applied after the primitive):
    Onion,
    Dilate,
    Displace,
}

/// <summary>
/// The presentation-only state of the fullscreen SDF debug subject: one primitive (with its numeric params + the shared
/// 2D-family lift), a stackable list of modifier ops, and an optional ground plane. It is PURE PRESENTATION — the
/// deterministic simulation never learns it exists; the overworld frame source composes it (through
/// <see cref="SdfDebugMode"/>) and rebuilds its program on a <see cref="Revision"/> bump. The op stack is capped at
/// <see cref="MaxOps"/> so the frame source's
/// worst-case capacity probe stays bounded.
/// </summary>
public sealed class SdfDebugScene {
    /// <summary>The op-stack cap — the frame source folds a worst-case stack of this many ops into its capacity probe,
    /// so a live push can never outgrow the engine's frozen buffers.</summary>
    public const int MaxOps = 12;

    /// <summary>The carve-pool cap — the frame source folds a worst-case pool of this many carves into its capacity
    /// probe (each carve is one static subtraction instance), so a live carve can never outgrow the engine's frozen
    /// buffers. Sized to sit comfortably under <see cref="SdfProgramBuilder.MaxInstances"/> (4096).</summary>
    public const int MaxCarves = 1024;

    /// <summary>The default carve radius when <c>sdf.carve</c> / the pad chord omit one.</summary>
    public const float DefaultCarveRadius = 0.35f;

    /// <summary>The default SmoothSubtraction radius a smooth carve uses when no <c>k</c> is given.</summary>
    public const float DefaultCarveSmoothK = 0.15f;

    private readonly List<SdfDebugOp> m_ops = [];
    private readonly List<SdfCarve> m_carves = [];
    private float[] m_params = DefaultParams(kind: SdfDebugShapeKind.Torus);
    private float[] m_params2 = [];

    /// <summary>The subject primitive.</summary>
    public SdfDebugShapeKind Shape { get; private set; } = SdfDebugShapeKind.Torus;

    /// <summary>The subject's numeric params (per-shape meaning — see <see cref="DefaultParams"/>).</summary>
    public IReadOnlyList<float> Params => m_params;

    /// <summary>The OPTIONAL second shape (the blend-debugging partner), or null for a single-subject program. The op
    /// stack applies to shape 1 ONLY (a per-shape stack is a later increment); shape 2 composes against shape 1's
    /// finished field through <see cref="Blend"/>.</summary>
    public SdfDebugShapeKind? Shape2 { get; private set; }

    /// <summary>The second shape's numeric params (same catalog/meanings as <see cref="Params"/>).</summary>
    public IReadOnlyList<float> Params2 => m_params2;

    /// <summary>The second shape's translation from the origin (default visibly offset so a fresh pair reads as two).</summary>
    public Vector3 Offset2 { get; private set; } = new(1.2f, 0f, 0f);

    /// <summary>How shape 2 composes against shape 1's field (union by default).</summary>
    public SdfBlendOp Blend { get; private set; } = SdfBlendOp.Union;

    /// <summary>The smooth/chamfer radius the smooth/chamfer blend families use (the hard blends ignore it).</summary>
    public float BlendSmooth { get; private set; } = 0.25f;

    /// <summary>The SLICE debug view's plane selector: 0 = camera-locked through the world origin (the default),
    /// 1/2/3 = a world X/Y/Z axis plane at <see cref="SliceOffset"/>. A PER-FRAME channel (it rides the frame's
    /// screen-light env lanes into the shader) — it never changes the program, so it does not bump <see cref="Revision"/>.</summary>
    public int SliceAxis { get; private set; }

    /// <summary>The axis-aligned slice plane's signed offset (world units; ignored while camera-locked).</summary>
    public float SliceOffset { get; private set; }

    /// <summary>The 2D-family lift mode (revolve/extrude) the lifted shapes use.</summary>
    public SdfLift Lift { get; private set; } = SdfLift.Revolve;

    /// <summary>The 2D-family lift amount (revolve offset, or extrude half-height).</summary>
    public float LiftAmount { get; private set; } = 0.5f;

    /// <summary>Whether a ground plane sits under the subject (default OFF — it contaminates the slice/iteration views).</summary>
    public bool Floor { get; private set; }

    /// <summary>Whether the subject is emitted inside a SCOPED field accumulator (<see cref="SdfProgramBuilder.PushField"/>/
    /// <see cref="SdfProgramBuilder.PopField"/>) — default ON. When ON, the stack's FIELD ops (onion/dilate/displace)
    /// shell the SUBJECT alone and the floor stays solid; when OFF the whole program shares one flat accumulator, so a
    /// field op shells EVERYTHING (the floor's zero-contour included) — the exact "a field op shells the whole scene"
    /// pathology the scoped accumulator fixes. The contrast between the two IS the debugging tool. Bumps the revision.</summary>
    public bool Scope { get; private set; } = true;

    /// <summary>The op stack (push order; point ops emit before the shape, field ops after — both in push order).</summary>
    public IReadOnlyList<SdfDebugOp> Ops => m_ops;

    /// <summary>The carve pool (append order) — each carve subtracts from the assembled subject+floor field, emitted
    /// LAST (higher segment indices than everything it bites; see <see cref="SdfDebugRenderer.Emit"/>).</summary>
    public IReadOnlyList<SdfCarve> Carves => m_carves;

    /// <summary>Bumped on every content change; the frame source rebuilds the program when it moves while the mode is up.</summary>
    public int Revision { get; private set; }

    /// <summary>Whether an op kind is a FIELD op (Onion/Dilate/Displace) — emitted AFTER the primitive.</summary>
    public static bool IsFieldOp(SdfDebugOpKind kind) =>
        (kind is SdfDebugOpKind.Onion or SdfDebugOpKind.Dilate or SdfDebugOpKind.Displace);

    /// <summary>Selects the subject primitive with an optional param override list; missing params take the shape's
    /// defaults. Bumps the revision.</summary>
    public void SetShape(SdfDebugShapeKind kind, IReadOnlyList<float> overrides) {
        ArgumentNullException.ThrowIfNull(overrides);

        var defaults = DefaultParams(kind: kind);

        for (var index = 0; ((index < overrides.Count) && (index < defaults.Length)); index++) {
            defaults[index] = overrides[index];
        }

        Shape = kind;
        m_params = defaults;
        Bump();
    }

    /// <summary>Selects (or replaces) the second shape with an optional param override list (same catalog as
    /// <see cref="SetShape"/>). Bumps the revision.</summary>
    public void SetShape2(SdfDebugShapeKind kind, IReadOnlyList<float> overrides) {
        ArgumentNullException.ThrowIfNull(overrides);

        var defaults = DefaultParams(kind: kind);

        for (var index = 0; ((index < overrides.Count) && (index < defaults.Length)); index++) {
            defaults[index] = overrides[index];
        }

        Shape2 = kind;
        m_params2 = defaults;
        Bump();
    }

    /// <summary>Removes the second shape (back to the single-subject program). Bumps the revision on change.</summary>
    /// <returns>Whether a second shape was present.</returns>
    public bool ClearShape2() {
        if (Shape2 is null) {
            return false;
        }

        Shape2 = null;
        m_params2 = [];
        Bump();

        return true;
    }

    /// <summary>Sets the second shape's translation. Bumps the revision.</summary>
    public void SetOffset2(Vector3 offset) {
        Offset2 = offset;
        Bump();
    }

    /// <summary>Sets how shape 2 composes against shape 1 (+ the smooth/chamfer radius). Bumps the revision.</summary>
    public void SetBlend(SdfBlendOp blend, float smooth) {
        Blend = blend;
        BlendSmooth = MathF.Max(0f, smooth);
        Bump();
    }

    /// <summary>Positions the slice plane (see <see cref="SliceAxis"/>). Per-frame channel — no revision bump.</summary>
    /// <param name="axis">0 = camera-locked, 1/2/3 = world X/Y/Z.</param>
    /// <param name="offset">The axis plane's signed offset (world units).</param>
    public void SetSlicePlane(int axis, float offset) {
        SliceAxis = Math.Clamp(value: axis, min: 0, max: 3);
        SliceOffset = offset;
    }

    /// <summary>Sets the 2D-family lift mode + amount. Bumps the revision.</summary>
    public void SetLift(SdfLift lift, float amount) {
        Lift = lift;
        LiftAmount = MathF.Max(0f, amount);
        Bump();
    }

    /// <summary>Toggles the ground plane. Bumps the revision.</summary>
    public void SetFloor(bool on) {
        Floor = on;
        Bump();
    }

    /// <summary>Toggles the scoped field accumulator (see <see cref="Scope"/>). Bumps the revision.</summary>
    public void SetScope(bool on) {
        Scope = on;
        Bump();
    }

    /// <summary>Pushes an op onto the stack (rejected when full). Bumps the revision on success.</summary>
    /// <returns>Whether the op was pushed (false = the stack is at <see cref="MaxOps"/>).</returns>
    public bool PushOp(SdfDebugOp op) {
        if (m_ops.Count >= MaxOps) {
            return false;
        }

        m_ops.Add(item: op);
        Bump();

        return true;
    }

    /// <summary>Pops the last-pushed op (no-op when empty). Bumps the revision on success.</summary>
    /// <returns>Whether an op was popped.</returns>
    public bool PopOp() {
        if (m_ops.Count == 0) {
            return false;
        }

        m_ops.RemoveAt(index: (m_ops.Count - 1));
        Bump();

        return true;
    }

    /// <summary>Clears the whole op stack (no-op when empty). Bumps the revision on success.</summary>
    /// <returns>How many ops were cleared.</returns>
    public int ClearOps() {
        var count = m_ops.Count;

        if (count > 0) {
            m_ops.Clear();
            Bump();
        }

        return count;
    }

    /// <summary>Appends a carve to the pool (rejected when full). Radius is clamped to a small positive minimum and the
    /// smooth radius to non-negative, so a bad number never packs a degenerate instance. Bumps the revision on success.</summary>
    /// <returns>Whether the carve was appended (false = the pool is at <see cref="MaxCarves"/>).</returns>
    public bool AddCarve(SdfCarve carve) {
        if (m_carves.Count >= MaxCarves) {
            return false;
        }

        m_carves.Add(item: carve with {
            Radius = MathF.Max(0.01f, carve.Radius),
            SmoothK = MathF.Max(0f, carve.SmoothK),
        });
        Bump();

        return true;
    }

    /// <summary>Removes the last-appended carve (no-op when empty). Bumps the revision on success.</summary>
    /// <returns>Whether a carve was removed.</returns>
    public bool PopCarve() {
        if (m_carves.Count == 0) {
            return false;
        }

        m_carves.RemoveAt(index: (m_carves.Count - 1));
        Bump();

        return true;
    }

    /// <summary>Clears the whole carve pool (no-op when empty). Bumps the revision on success.</summary>
    /// <returns>How many carves were cleared.</returns>
    public int ClearCarves() {
        var count = m_carves.Count;

        if (count > 0) {
            m_carves.Clear();
            Bump();
        }

        return count;
    }

    /// <summary>Formats one carve as the shared echo fragment <c>@(x,y,z) r=… [smooth k=…]</c> — used by BOTH the
    /// <c>sdf.carve</c> verb and the pad-chord echo so a scripted carve and a pad carve read (and ARE) the same data.</summary>
    public static string FormatCarve(SdfCarve carve) =>
        string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"@({carve.Center.X:0.##},{carve.Center.Y:0.##},{carve.Center.Z:0.##}) r={carve.Radius:0.###}{(carve.Smooth ? $" smooth k={carve.SmoothK:0.###}" : "")}");

    /// <summary>The per-shape default numeric params (a fresh array — the caller owns it). The count is the shape's
    /// param arity; the meanings mirror the <see cref="SdfProgramBuilder"/> signatures.</summary>
    public static float[] DefaultParams(SdfDebugShapeKind kind) {
        return kind switch {
            SdfDebugShapeKind.Sphere => [1f],                       // radius
            SdfDebugShapeKind.Box => [0.8f, 0.8f, 0.8f, 0.05f],     // halfX halfY halfZ round
            SdfDebugShapeKind.Torus => [1f, 0.35f],                 // major minor
            SdfDebugShapeKind.Capsule => [1f, 0.35f],               // halfLength radius (endpoint = (0, 2*halfLength, 0))
            SdfDebugShapeKind.Cylinder => [0.7f, 1f],               // radius halfHeight
            SdfDebugShapeKind.Ellipsoid => [1f, 0.7f, 0.5f],        // radiusX radiusY radiusZ
            SdfDebugShapeKind.Vesica => [1f, 0.5f],                 // radius halfSeparation
            SdfDebugShapeKind.RoundCone => [0.7f, 0.3f, 1.2f],      // lowerRadius upperRadius height
            SdfDebugShapeKind.RoundedRect => [0.8f, 0.5f, 0.15f],   // halfWidth halfHeight cornerRadius (lifted)
            SdfDebugShapeKind.Polygon => [6f, 0.9f],                // sides radius (lifted)
            SdfDebugShapeKind.Star => [5f, 0.9f, 2.6f],             // points radius sharpness (lifted)
            SdfDebugShapeKind.Trapezoid => [0.8f, 0.4f, 0.7f],      // bottomHalfW topHalfW halfHeight (lifted)
            SdfDebugShapeKind.Ellipse => [0.9f, 0.6f],              // semiX semiY (lifted)
            _ => [1f],
        };
    }

    /// <summary>Whether the given shape is a 2D-family primitive lifted by <see cref="Lift"/>/<see cref="LiftAmount"/>.</summary>
    public static bool IsLiftedShape(SdfDebugShapeKind kind) =>
        (kind is SdfDebugShapeKind.RoundedRect or SdfDebugShapeKind.Polygon or SdfDebugShapeKind.Star or SdfDebugShapeKind.Trapezoid or SdfDebugShapeKind.Ellipse);

    private void Bump() => Revision++;
}
