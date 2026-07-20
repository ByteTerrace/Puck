using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Queries;

namespace Puck.Demo.Gravity;

/// <summary>
/// Defines a small, walkable planetoid: one sphere, a few
/// <see cref="SdfBlendOp.SmoothUnion"/> mounds, one <see cref="SdfBlendOp.SmoothSubtraction"/> crater, all WARP-FREE
/// (every op/shape here is in <see cref="SdfFieldEvaluator"/>'s interpreted subset — see that type's KEEP-IN-SYNC
/// header; a warped op would throw at <see cref="BuildEvaluator"/>'s construction). Authored content only, mirroring
/// <see cref="Puck.Demo.Rts.RtsScenario"/>'s split: this type owns the planetoid's geometry and bakes the ONE
/// <see cref="IFieldEvaluator"/> the sim's <see cref="Puck.Demo.Overworld.FieldWalkerBody"/> walks; the actual
/// per-tick walker state lives on <see cref="Puck.Demo.Overworld.OverworldWorld"/> (its <c>FieldWalker*</c> members
/// and <c>AdvanceFieldWalker</c>).
/// <para>
/// SINGLE SOURCE OF TRUTH (the arc's thesis made literal): <see cref="BuildProgram"/> is called exactly once by
/// <see cref="BuildEvaluator"/> AND once by <see cref="PlanetoidEmitter"/> — the fixed-point evaluator the walker
/// steps against and the GPU program the renderer marches are built from the SAME instruction stream, not two
/// hand-synchronized authoring paths.
/// </para>
/// <para>
/// PLACEMENT: the planetoid sits at <see cref="PlanetCenter"/>, a FAR <see cref="WorldCoord3"/> cell (distinct from
/// the room's own far-cell precedent — see <c>OverworldWorld</c>'s spawn-cell remarks) — exercising the planet-scale
/// coordinate seam. Only
/// <see cref="WorldCoord3.Local"/> feeds <see cref="SdfFieldEvaluator.TryDistance"/> (see its own remarks) — the
/// walker's <see cref="WorldCoord3"/> stays in THIS SAME cell for the whole proof (a planetoid of radius
/// <see cref="PlanetRadius"/> never approaches a cell boundary), so cell bookkeeping never needs to cross a seam
/// mid-walk; the far placement alone already proves the cell arithmetic (<see cref="WorldCoord3.Normalize"/>,
/// <see cref="WorldCoord3.Delta"/>, <see cref="WorldCoord3.ToRenderRelative"/>) holds far from the world origin,
/// exactly like the room's own far-cell boot already proves for the overworld.
/// </para>
/// <para>
/// THE WALKING GREAT CIRCLE: every mound/crater is placed OFF the equatorial (XZ) plane (a latitude offset), so the
/// walker's own equatorial circumnavigation (see <see cref="StartPose"/>) always crosses the plain sphere — clean,
/// hand-provable tick-to-longitude math for the headline script's quadrant assertions, with the mounds/crater purely
/// a visual "this reads as a planetoid, not a bare ball" proof elsewhere on the surface.
/// </para>
/// </summary>
public static class GravityScenario {
    /// <summary>The planetoid's far cell (X and Z) — a far location (1,000,000,000) proving the seam is a general
    /// coordinate capability, not a single hard-coded location. Y stays at the origin cell (the planetoid has no reason
    /// to sit far vertically).</summary>
    public const long PlanetCellX = 1_000_000_000L;
    /// <summary>See <see cref="PlanetCellX"/>.</summary>
    public const long PlanetCellY = 0L;
    /// <summary>See <see cref="PlanetCellX"/>.</summary>
    public const long PlanetCellZ = 1_000_000_000L;

    /// <summary>The planetoid's base sphere radius — reads as a small planet next to the avatar token (a standing
    /// figure a couple of world units tall), judged visually against <see cref="WalkerInstanceEmitter"/>'s capsule
    /// during the framing pass.</summary>
    public const float PlanetRadius = 7f;

    // Three SmoothUnion mounds, at latitudes off the equator (see the type remarks) — directions chosen spread
    // around the sphere so the planetoid reads as textured from every framing angle, not just one hemisphere.
    private static readonly Vector3 Mound1Direction = Vector3.Normalize(value: new Vector3(x: 0.30f, y: 0.85f, z: -0.30f));
    private static readonly Vector3 Mound2Direction = Vector3.Normalize(value: new Vector3(x: -0.60f, y: 0.55f, z: 0.55f));
    private static readonly Vector3 Mound3Direction = Vector3.Normalize(value: new Vector3(x: 0.20f, y: -0.80f, z: 0.55f));

    private const float MoundRadius = (PlanetRadius * 0.35f);
    private const float MoundEmbed = (MoundRadius * 0.30f); // how far the mound's center sits inside the base sphere
    private const float MoundSmooth = 0.9f;

    // One SmoothSubtraction crater, also off the equator.
    private static readonly Vector3 CraterDirection = Vector3.Normalize(value: new Vector3(x: -0.45f, y: -0.70f, z: -0.55f));

    private const float CraterRadius = (PlanetRadius * 0.35f);
    private const float CraterProud = (CraterRadius * 0.6f); // how far the crater sphere's center sits OUTSIDE the base sphere
    private const float CraterSmooth = 0.6f;

    /// <summary>The planetoid's center — <see cref="PlanetCellX"/>/<see cref="PlanetCellY"/>/<see cref="PlanetCellZ"/>
    /// at zero local offset. Every position this scenario authors (the sphere, the mounds, the crater, a walker's
    /// spawn — see <see cref="StartPose"/>) is expressed relative to this point.</summary>
    public static WorldCoord3 PlanetCenter => new(CellX: PlanetCellX, CellY: PlanetCellY, CellZ: PlanetCellZ, Local: FixedVector3.Zero);

    /// <summary>Authors the planetoid's <see cref="SdfProgram"/> — the SAME program <see cref="BuildEvaluator"/>
    /// wraps and <see cref="PlanetoidEmitter"/> renders (the arc's single-source-of-truth thesis). Every op/shape is
    /// warp-free (plain <see cref="SdfProgramBuilder.ResetPoint"/>/<see cref="SdfProgramBuilder.Translate(Vector3)"/>
    /// + <see cref="SdfProgramBuilder.Sphere"/> with a <see cref="SdfBlendOp.SmoothUnion"/>/
    /// <see cref="SdfBlendOp.SmoothSubtraction"/> blend — no <see cref="SdfProgramBuilder.Rotate"/>, no domain warp),
    /// so <see cref="SdfFieldEvaluator"/>'s constructor never throws over it.</summary>
    /// <returns>The planetoid's program, local to <see cref="PlanetCenter"/> (i.e. authored around the local origin —
    /// a caller places it in the world by choosing which <see cref="WorldCoord3"/> cell to evaluate/render it in).</returns>
    public static SdfProgram BuildProgram() {
        var builder = new SdfProgramBuilder();

        EmitInto(builder: builder);

        return builder.Build();
    }

    /// <summary>Emits the planetoid's geometry into an ALREADY-OPEN builder — the shared authoring code
    /// <see cref="BuildProgram"/> (a standalone program, for <see cref="BuildEvaluator"/>) and
    /// <see cref="PlanetoidEmitter"/> (composed into the gravity scene's shared builder) both call, so there is
    /// exactly ONE place the planetoid's instruction stream is written (the single-source-of-truth thesis, made
    /// literal at the authoring level, not just "two builds that happen to agree").</summary>
    /// <param name="builder">The builder to emit into (may already carry other content).</param>
    public static void EmitInto(SdfProgramBuilder builder) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        var rockMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.50f, y: 0.46f, z: 0.40f), Specular: 0.08f, Shininess: 6f));

        // The base sphere — the accumulator's FIRST candidate (plain Union), so every later shape composes against it.
        _ = builder.ResetPoint().Sphere(radius: PlanetRadius, material: rockMaterial);

        EmitMound(builder: builder, direction: Mound1Direction, material: rockMaterial);
        EmitMound(builder: builder, direction: Mound2Direction, material: rockMaterial);
        EmitMound(builder: builder, direction: Mound3Direction, material: rockMaterial);

        // The crater: a SmoothSubtraction is LOCAL (see the sdf-world skill's accumulator rule — subtraction only
        // bites inside its own shape), safe to emit anywhere in the chain, unlike an intersection.
        var craterCenter = (CraterDirection * (PlanetRadius + CraterProud));

        _ = builder.ResetPoint().Translate(offset: craterCenter).Sphere(radius: CraterRadius, material: rockMaterial, blend: SdfBlendOp.SmoothSubtraction, smooth: CraterSmooth);
    }

    private static void EmitMound(SdfProgramBuilder builder, Vector3 direction, int material) {
        var center = (direction * (PlanetRadius - MoundEmbed));

        _ = builder.ResetPoint().Translate(offset: center).Sphere(radius: MoundRadius, material: material, blend: SdfBlendOp.SmoothUnion, smooth: MoundSmooth);
    }

    /// <summary>Builds the ONE <see cref="IFieldEvaluator"/> the walker steps against, from <see cref="BuildProgram"/>'s
    /// exact program — the single source of truth <c>OverworldFrameSource</c>'s constructor calls once and binds via
    /// <c>OverworldWorld.ConfigureFieldEvaluator</c>, mirroring <see cref="Puck.Demo.Rts.RtsScenario.BuildQuery"/>.</summary>
    /// <returns>The bound evaluator.</returns>
    public static IFieldEvaluator BuildEvaluator() =>
        new SdfFieldEvaluator(program: BuildProgram());

    /// <summary>The walker's spawn pose at a given EQUATORIAL longitude (degrees, measured from <c>+X</c> toward
    /// <c>+Z</c> — see the type remarks' "walking great circle"): position on the sphere's surface plus the radial
    /// "up" guess <see cref="Puck.Demo.Overworld.FieldWalkerBody"/>'s ctor wants before its first
    /// <see cref="Puck.Demo.Overworld.FieldWalkerBody.Step"/> ever queries the field.</summary>
    /// <param name="longitudeDegrees">The starting longitude, degrees.</param>
    /// <returns>The spawn position (in <see cref="PlanetCenter"/>'s cell) and initial up.</returns>
    public static (WorldCoord3 Position, FixedVector3 Up) StartPose(float longitudeDegrees) {
        var radians = (longitudeDegrees * (MathF.PI / 180f));
        var direction = new Vector3(x: MathF.Cos(x: radians), y: 0f, z: MathF.Sin(x: radians));
        var up = ToFixed(value: direction);
        var local = ToFixed(value: (direction * PlanetRadius));
        var position = PlanetCenter.WithLocal(local: local);

        return (position, up);
    }

    /// <summary>The equatorial longitude (degrees, the SAME convention <see cref="StartPose"/> uses) of a position
    /// relative to <see cref="PlanetCenter"/> — the headline script's own "which quadrant is the walker in" readout,
    /// computed from the XZ plane regardless of how far the walker's Y has drifted off the exact equator (it never
    /// should, but this stays honest even if it does).</summary>
    /// <param name="position">The position to measure.</param>
    /// <returns>The longitude in degrees, in <c>(-180, 180]</c>.</returns>
    public static double LongitudeDegrees(WorldCoord3 position) {
        var delta = position.Delta(origin: PlanetCenter);
        var radians = Math.Atan2(y: (double)delta.Z, x: (double)delta.X);

        return (radians * (180.0 / Math.PI));
    }

    // FromDouble per-component — the standard float(presentation)->fixed(sim seed) seam FieldWalkerBody/RtsScenario
    // both already use for authored constants.
    private static FixedVector3 ToFixed(Vector3 value) =>
        new(X: FixedQ4816.FromDouble(value: value.X), Y: FixedQ4816.FromDouble(value: value.Y), Z: FixedQ4816.FromDouble(value: value.Z));
}
