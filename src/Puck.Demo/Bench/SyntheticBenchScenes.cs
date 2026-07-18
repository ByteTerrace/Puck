using System.Numerics;

using Puck.Bench;
using Puck.Demo.Overworld;
using Puck.SdfVm;
using Puck.SdfVm.Debug;

namespace Puck.Demo.Bench;

/// <summary>
/// Builds the FIVE synthetic engine-bench workloads (the <c>sdf.shapes</c>/<c>sdf.ops</c>/<c>sdf.carves</c>/
/// <c>sdf.storm</c>/<c>sdf.instances</c> scenes) as ready-to-render SDF programs. Each is ONE fixed rung of the
/// corresponding <see cref="SdfBenchScene"/> ladder, built through the SHARED <see cref="SdfBenchWorkloads"/> config
/// builders and emitted with the SAME <see cref="SdfDebugRenderer.EmitBench"/> the live <c>sdf.bench</c> ladder uses —
/// so the two never fork. The demo's frame source renders the returned program as a direct fullscreen takeover (the AGB
/// takeover's single-cached-program shape) and the content-blind <see cref="Puck.Bench.BenchRuntime"/> harness samples
/// it through the ordinary <see cref="Puck.Abstractions.Gpu.IPassTimingSource"/> seam — no <c>sdf.bench</c> ladder run,
/// no state-machine coupling.
/// <para>
/// This helper — NOT the frame source — names every bench-workload type (<see cref="SdfDebugRenderer"/>,
/// <see cref="SdfBenchWorkloads"/>, <see cref="SdfBenchConfig"/>, the shape/op/family enums), so the frame source, at
/// its exact analyzer coupling ceiling, holds only the built <see cref="SdfProgram"/> + its transforms + a framing pose.
/// </para>
/// </summary>
internal static class SyntheticBenchWorkloads {
    // The single GPU-meaningful instance/carve/storm rung (big enough to read on the GPU, small enough to keep the
    // suite under a minute) — the plan's "1024 rung".
    private const int Rung = 1024;
    // The single-fullscreen-subject framing distance (shapes/ops/carves shrink or hold a ~2-unit subject — a fixed
    // distance keeps the camera dead still). Mirrors SdfBenchScene.SingleShapeDistance.
    private const float SingleShapeDistance = 4.8f;
    // The instance-grid geometry (mirrors SdfBenchScene.InstanceSpacing / a covering per-copy bound) — used to FRAME
    // the grid and to lay the storm rung's per-frame dynamic transforms.
    private const float InstanceSpacing = 1.25f;
    private const float InstanceBoundRadius = 0.6f;
    // The bench camera's fixed 3/4 orbit (no pad dependence) — mirrors SdfBenchScene's OrbitYaw/OrbitPitch so the
    // synthetic scenes frame their workload exactly as the live sdf.bench ladder does.
    private const float OrbitYaw = 0.6f;
    private const float OrbitPitch = 0.55f;
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));    // matches ScreenLayoutDirector.FieldOfViewRadians
    private const float FrameMargin = 1.15f;
    private const float MinCameraDistance = 3.5f;

    /// <summary>One built synthetic workload: the program, its per-frame dynamic transforms (empty for a static
    /// workload; a spread grid for the storm rung), and the fixed pose that frames it.</summary>
    /// <param name="Program">The workload's SDF program (build ONCE; the caller re-arms the same instance each frame).</param>
    /// <param name="Transforms">The per-frame dynamic transforms (empty when the program has no dynamic slots).</param>
    /// <param name="Target">The framing look target (world origin — every workload is authored centred).</param>
    /// <param name="Yaw">The framing yaw (radians).</param>
    /// <param name="Pitch">The framing pitch (radians).</param>
    /// <param name="Distance">The framing distance (world units).</param>
    /// <param name="CarveBakeConfig">For the <c>sdf.carves</c> workload ONLY: the bench config the scene re-emits
    /// through a settle-0 carve-bake planner so <c>sdf.carve-bake on</c> measures the BAKED steady state (carve-bake
    /// plan §4 "the bench carves scene"). Null for every other workload — they render the fixed <see cref="Program"/>.</param>
    public readonly record struct Workload(SdfProgram Program, DynamicTransform[] Transforms, Vector3 Target, float Yaw, float Pitch, float Distance, SdfBenchConfig? CarveBakeConfig = null);

    /// <summary>The SHAPES rung: one representative primitive (a torus) fullscreen — the shape-evaluation shading cost.</summary>
    public static Workload Shapes() => Fullscreen(config: SdfBenchWorkloads.Shape(shape: SdfDebugShapeKind.Torus));

    /// <summary>The OPS rung: a torus behind one point warp (a twist) — the marginal cost of a Lipschitz-clamped warp.</summary>
    public static Workload Ops() => Fullscreen(config: SdfBenchWorkloads.Op(op: SdfBenchOp.Twist));

    /// <summary>The CARVES rung: a fixed subject bitten by <see cref="Rung"/> clustered subtraction carves — the honest
    /// runtime-carving views-cost worst case.</summary>
    public static Workload Carves() {
        var config = SdfBenchWorkloads.Carve(family: SdfBenchCarveFamily.Clustered, count: Rung);

        return new Workload(Distance: SingleShapeDistance, Pitch: OrbitPitch, Program: Build(config: config), Target: Vector3.Zero, Transforms: [], Yaw: OrbitYaw, CarveBakeConfig: config);
    }

    /// <summary>The INSTANCES rung: <see cref="Rung"/> real (grid-culled, static) sphere instances in a 3D grid — the
    /// uniform-grid instance-cull path.</summary>
    public static Workload Instances() => GridStatic(config: SdfBenchWorkloads.Instances(shape: SdfDebugShapeKind.Sphere, count: Rung), count: Rung);

    /// <summary>The STORM rung: <see cref="Rung"/> DYNAMIC sphere instances (the always-tested beam list — the storm
    /// cliff). Each rides its own dynamic-transform slot; the transforms are laid out as a spread grid so the copies are
    /// framed and correctly bounded (a benchmark wants a stable, comparable workload — a static-but-dynamic-instance
    /// grid measures the identical O(dynamic-n) flat-list beam cost as a moving one).</summary>
    public static Workload Storm() {
        var config = SdfBenchWorkloads.StormRung(mode: SdfBenchStormMode.Motion, count: Rung);
        var program = Build(config: config);
        var slots = Math.Max(val1: Rung, val2: program.RequiredDynamicTransformCapacity);
        var transforms = PackGrid(count: slots);

        return new Workload(Distance: GridDistance(count: Rung), Pitch: OrbitPitch, Program: program, Target: Vector3.Zero, Transforms: transforms, Yaw: OrbitYaw);
    }

    // A single fullscreen subject (shapes/ops/carves): no dynamic slots, the fixed close framing.
    private static Workload Fullscreen(SdfBenchConfig config) =>
        new(Distance: SingleShapeDistance, Pitch: OrbitPitch, Program: Build(config: config), Target: Vector3.Zero, Transforms: [], Yaw: OrbitYaw);

    // A static instance grid (instances): no dynamic slots, the grid-framing distance.
    private static Workload GridStatic(SdfBenchConfig config, int count) =>
        new(Distance: GridDistance(count: count), Pitch: OrbitPitch, Program: Build(config: config), Target: Vector3.Zero, Transforms: [], Yaw: OrbitYaw);

    // Builds a workload program through the SAME emitter the live sdf.bench ladder uses (no fork).
    private static SdfProgram Build(SdfBenchConfig config) {
        var builder = new SdfProgramBuilder();

        new SdfDebugRenderer().EmitBench(builder: builder, config: config);

        return builder.Build();
    }

    // The per-axis cell count of the smallest cube grid holding count copies (⌈∛count⌉) — mirrors SdfBenchScene.GridDimension.
    private static int GridDimension(int count) => Math.Max(val1: 1, val2: (int)MathF.Ceiling(x: MathF.Cbrt(x: MathF.Max(x: 1f, y: count))));

    // The orbit distance that frames the whole instance grid inside the FOV, with slack and a floor — mirrors SdfBenchScene.ComputeDistance.
    private static float GridDistance(int count) {
        var grid = GridDimension(count: count);
        var halfExtent = (((grid - 1) * InstanceSpacing) * 0.5f);
        var workloadRadius = (halfExtent + InstanceBoundRadius);
        var distance = ((workloadRadius / MathF.Tan(x: (FieldOfViewRadians * 0.5f))) * FrameMargin);

        return MathF.Max(x: MinCameraDistance, y: distance);
    }

    // Lays count dynamic transforms as a centred 3D grid (spacing InstanceSpacing) so the storm rung's dynamic instances
    // are spread, framed, and each covered by its own bound (the bound centre resolves as slot position + zero offset).
    private static DynamicTransform[] PackGrid(int count) {
        var transforms = new DynamicTransform[count];
        var grid = GridDimension(count: count);
        var half = (((grid - 1) * InstanceSpacing) * 0.5f);

        for (var index = 0; (index < count); index++) {
            var ix = (index % grid);
            var iy = ((index / grid) % grid);
            var iz = (index / (grid * grid));

            transforms[index] = new DynamicTransform(
                Orientation: Quaternion.Identity,
                Position: new Vector3(
                    x: ((ix * InstanceSpacing) - half),
                    y: ((iy * InstanceSpacing) - half),
                    z: ((iz * InstanceSpacing) - half)
                )
            );
        }

        return transforms;
    }
}

/// <summary>
/// One synthetic engine-bench scene controller (<c>sdf.shapes</c>/<c>sdf.ops</c>/<c>sdf.carves</c>/<c>sdf.storm</c>/
/// <c>sdf.instances</c>, and the weight-0 <c>warmup</c> which reuses the storm rung). It swaps a fixed workload PROGRAM
/// in through the frame source's bench-takeover seam every produced frame (one-frame sticky — a scene that stops driving
/// auto-releases, so there is no teardown verb) and lets the harness sample it through the ordinary per-pass timing
/// seam. No setup/teardown scripts: the takeover replaces the whole screen (the frame source forces the room to
/// fullscreen while a workload is armed), so no room state needs staging.
/// </summary>
internal sealed class SyntheticBenchScene : IBenchSceneController {
    // Lazily resolved: the frame source does not exist until the overworld node's first ProduceFrame, but the harness
    // registers scenes at composition (installer StartAsync) — by the time a run's OnFrame executes it is built.
    private readonly Func<OverworldFrameSource?> m_frameSource;
    private readonly SyntheticBenchWorkloads.Workload m_workload;
    // CARVE-BAKE (carve-bake plan §4 "the bench carves scene"): the sdf.carves workload owns a settle-0 planner + its
    // own renderer, re-emits the carve program THROUGH the planner (so an adopted bin renders as ONE SampledRegion
    // brick), and drives the planner off the engine's per-frame AdvanceBricks via the frame source's registered hook —
    // so `sdf.carve-bake on` measures the BAKED steady state. Null for every other workload (they arm the fixed
    // pre-built program and are ready immediately). Built lazily once the frame source exists.
    private readonly SdfCarveBakePlanner? m_planner;
    private readonly SdfDebugRenderer? m_renderer;
    private readonly SdfBenchConfig m_carveBakeConfig;
    private readonly bool m_carveBake;
    private SdfProgram m_program;
    private bool m_advanceRegistered;

    /// <summary>Creates the controller over a lazy frame-source resolver and a pre-built workload.</summary>
    /// <param name="frameSource">Resolves the overworld frame source (the bench-takeover composition point) — null
    /// until the node's first frame, by which point every bench run has started.</param>
    /// <param name="workload">The fixed workload this scene renders (built once, re-armed each frame).</param>
    public SyntheticBenchScene(Func<OverworldFrameSource?> frameSource, SyntheticBenchWorkloads.Workload workload) {
        ArgumentNullException.ThrowIfNull(argument: frameSource);

        m_frameSource = frameSource;
        m_workload = workload;
        m_program = workload.Program;

        if (workload.CarveBakeConfig is { } config) {
            m_carveBake = true;
            m_carveBakeConfig = config;
            m_planner = new SdfCarveBakePlanner(settleFrames: 0);
            m_renderer = new SdfDebugRenderer();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> SetupScript => [];

    /// <inheritdoc/>
    public IReadOnlyList<string> TeardownScript => [];

    /// <inheritdoc/>
    public void OnFrame(int frameIndex) =>
        m_frameSource()?.ArmBenchWorkload(
            distance: m_workload.Distance,
            pitch: m_workload.Pitch,
            program: m_program,
            target: m_workload.Target,
            transforms: m_workload.Transforms,
            yaw: m_workload.Yaw
        );

    /// <inheritdoc/>
    public bool IsReady() {
        // Non-carve-bake scenes render a fixed pre-built program — ready the instant they begin.
        if (!m_carveBake) {
            return true;
        }

        if (m_frameSource() is not { } frameSource) {
            return false;
        }

        // OFF: the planner would emit analytic — byte-identical to today. Don't even register the advance hook (it would
        // re-bin the cluster every frame for nothing); the fixed pre-built program is ready immediately.
        if (!SdfCarveBakePlanner.Enabled) {
            return true;
        }

        // Register the per-frame planner-advance hook once the frame source exists (it runs off AdvanceBricks, the only
        // seam that carries the engine's brick-bake service). Runs through the harness's await window so the background
        // bake completes before sampling.
        if (!m_advanceRegistered) {
            frameSource.SetBenchBrickAdvance(advance: AdvanceBake);
            m_advanceRegistered = true;
        }

        // ON: wait until the cluster's brick has baked and adopted (no bin still baking) — the sampled window then
        // measures the O(1) brick, not the mid-bake analytic frames.
        var (_, baking, brick) = m_planner!.PhaseCounts;

        return ((brick >= 1) && (baking == 0));
    }

    // The registered AdvanceBricks hook: advance the settle-0 planner against the live engine and, on a handoff (a bin
    // adopting or releasing a brick), rebuild + re-arm the takeover program so it emits the SampledRegion in place of
    // the cluster's analytic carves. A constant carve revision — the synthetic carve list never edits.
    private void AdvanceBake(Puck.SdfVm.ISdfBrickBakeService bakes) {
        if (m_renderer!.AdvanceBenchCarveBake(config: m_carveBakeConfig, planner: m_planner!, carveRevision: 0, bakes: bakes)) {
            var builder = new SdfProgramBuilder();

            m_renderer.EmitBench(builder: builder, config: m_carveBakeConfig, carvePlanner: m_planner);
            m_program = builder.Build();
        }
    }
}
