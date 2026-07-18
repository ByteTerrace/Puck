using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Maths;

namespace Puck.SdfVm.Debug;

/// <summary>The workload family a bench run measures — <see cref="Shapes"/> (each debuggable primitive fullscreen),
/// <see cref="Ops"/> (a fixed torus + one modifier, marginal-cost vs the bare subject), <see cref="Instances"/>
/// (a 3D grid of N real instances of one shape), <see cref="Carves"/> (a fixed subject + N subtraction carves in a
/// placement family — the runtime-carving cost profile), and <see cref="Storm"/> (the motion/churn ladder — every
/// instance moves, or the whole program rebuilds, each produced frame; see <see cref="SdfBenchStormMode"/>).</summary>
public enum SdfBenchWorkload {
    Shapes,
    Ops,
    Instances,
    Rigs,
    Carves,
    Storm,
}

/// <summary>The churn axis a <see cref="SdfBenchWorkload.Storm"/> config exercises. <see cref="Motion"/> — N DYNAMIC
/// instances that ALL move per produced frame through the per-frame dynamic-transform buffer: the host bins only
/// STATIC instances into the uniform grid, so a per-frame-moving instance rides the FLAT always-tested list and beam
/// returns O(moving-n) BY DESIGN — this rung is that cliff made measurable (the GPU-built-grid fork needs it before the
/// machine-fleet arc). <see cref="Rebuild"/> — the SAME counts of STATIC instances but a full program REBUILD every
/// produced frame (a per-frame revision bump forces BuildProgram + UploadProgram + the packer), so the ladder measures
/// the authoring/carve path's upload+pack ceiling at scale. <see cref="Camera"/> — one mid-size STATIC workload whose
/// bench camera pose orbits a FULL REVOLUTION across the sample window (still deterministic per produced frame), the
/// re-cull cost of a moving view over a still scene. The mode rides each config's label so the table reads it back
/// (<c>storm x1024</c>, <c>storm rebuild x1024</c>, <c>storm camera x1024</c>).
/// <para>A future <c>monitors</c> rung may measure N small viewports refreshing round-robin; viewport
/// right-sizing does not exist yet (naive 32-slot scaling is a 262 MB mask buffer at the instance cap), so the honest
/// aggregate-of-per-viewport-fixed-costs measurement waits on that work (docs/sdf-backlog.md §the many-eyes arc).</para></summary>
public enum SdfBenchStormMode {
    Motion,
    Rebuild,
    Camera,
}

/// <summary>The placement family a <see cref="SdfBenchWorkload.Carves"/> run uses — <see cref="Clustered"/> (carves
/// packed on the subject surface, densely overlapping the same tiles: the honest views-cost worst case),
/// <see cref="Scattered"/> (spread through empty space + the floor, mostly masking out: the beam-wall control where the
/// beam grows O(n) while views stays flat), and <see cref="Smooth"/> (clustered SmoothSubtraction — halo × mask-width
/// pressure). The family is baked into each config's label so the table reads it back (e.g. <c>carves clustered x256</c>).</summary>
public enum SdfBenchCarveFamily {
    Clustered,
    Scattered,
    Smooth,
}

/// <summary>The op catalog the <see cref="SdfBenchWorkload.Ops"/> workload steps through — the <see cref="Baseline"/>
/// (a bare torus behind a single identity Translate, so every other row measures ONE extra instruction against it)
/// plus one representative of each modifier family. This is a SUPERSET of <see cref="SdfDebugOpKind"/> (it adds
/// <see cref="Wallpaper"/>, which the debug op-stack does not carry), so the bench owns its own op-application switch
/// in <see cref="SdfDebugRenderer"/> rather than reusing the debug stack.</summary>
public enum SdfBenchOp {
    Baseline,
    Twist,
    BendX,
    Elongate,
    Repeat,
    RepeatLimited,
    Polar,
    Symmetry,
    Wallpaper,
    LogSphere,
    CellJitter,
    Displace,
    DomainWarp,
    Onion,
    Dilate,
    Scale,
}

/// <summary>One measurable configuration in a bench run: a label for its table row plus the union of parameters the
/// workloads need (each reads only its own — Shapes reads <see cref="Shape"/>, Ops reads <see cref="Op"/>, Instances/
/// Carves read <see cref="InstanceCount"/>, Carves also reads <see cref="CarveFamily"/>). A run is an ordered list of
/// these; the runner emits, warms, and samples each in turn. <see cref="CarveFamily"/> defaults so the non-carve call
/// sites stay unchanged.</summary>
public readonly record struct SdfBenchConfig(string Label, SdfBenchWorkload Workload, SdfDebugShapeKind Shape, SdfBenchOp Op, int InstanceCount, SdfBenchCarveFamily CarveFamily = SdfBenchCarveFamily.Clustered, SdfBenchStormMode StormMode = SdfBenchStormMode.Motion);

/// <summary>One configuration's measured timing: the median plus min/max of each per-pass GPU-ms channel over the
/// sample window. <see cref="HasTimings"/> is false when the window collected no timing samples (GPU timing not armed,
/// or no timestamps) — the report says so loudly instead of printing zeros.</summary>
public readonly record struct SdfBenchResult(
    string Label,
    int InstanceCount,
    bool HasTimings,
    double FrameMed, double FrameMin, double FrameMax,
    double BeamMed, double BeamMin, double BeamMax,
    double ViewsMed, double ViewsMin, double ViewsMax,
    double CompositeMed, double CompositeMin, double CompositeMax
);

/// <summary>
/// The SDF performance-bench runner — an ASYNC per-frame state machine that lives INSIDE the SDF-debug mode (composed
/// into <c>SdfDebugMode</c> beside the scene). It cannot block <c>CaptureFrame</c>, so it advances ONE step per
/// produced frame (<see cref="Advance"/>, called from the render node with the previous frame's per-pass GPU ms): for
/// each configuration it (a) selects the config — bumping <see cref="Revision"/> so the frame source rebuilds the
/// program to that workload, (b) waits <see cref="WarmFrames"/> warm-up frames (the first dispatch of a fresh pipeline
/// showed a ~40 ms compile stall), (c) samples <see cref="SampleFrames"/> measured frames, and (d) records the median
/// + min/max of frame/beam/views/composite. When the last config finishes it prints a fixed-width table to stdout.
/// <para>
/// While a run is <see cref="Running"/> the mode's program IS the current config's workload (the debug subject is
/// suppressed) and the camera is a FIXED deterministic pose framing the whole workload (no pad dependence) — see
/// <see cref="CameraFrame"/>. Presentation only; the deterministic simulation never sees the bench.
/// </para>
/// </summary>
public sealed class SdfBenchScene {
    // The fixed orbit pose every bench camera uses (no pad dependence) — a gentle 3/4 view. Distance is COMPUTED per
    // config to frame the whole workload (ComputeDistance); yaw/pitch are constant so a sweep's per-config reframes are
    // pure dolly moves.
    private const float OrbitYaw = 0.6f;
    private const float OrbitPitch = 0.55f;
    // The render kernels march to MaxDistance = 60 world units (sdf-world.hlsli); keep the framed workload's far corner
    // comfortably inside that so no instance escapes unrendered.
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f)); // matches ScreenLayoutDirector.FieldOfViewRadians
    private const float FrameMargin = 1.15f;                            // slack so the workload never touches the view edge
    private const float MinCameraDistance = 3.5f;
    // The single-shape workloads (shapes/ops) frame a ~2-unit-radius subject; a fixed distance keeps the camera dead
    // still across the whole run (no reframe between configs).
    private const float SingleShapeDistance = 4.8f;

    // The instance-grid geometry: a compact per-instance size, a spacing that leaves an air gap between neighbours, and
    // a cull bound that covers every catalogued shape's compact form with margin (see SdfDebugRenderer.EmitInstances).
    internal const float InstanceSpacing = 1.25f;
    internal const float InstanceBoundRadius = 0.6f;

    // The STORM ladder (the motion/churn family). Tops out at 4096 = the dynamic-transform capacity floor the render
    // assembly reserves for the mode (SdfDebugMode.WorstCaseDynamicTransformCapacity), so a storm run can never ask
    // for more moving slots than the engine was constructed with. Public: SdfDebugMode (Puck.Demo) reads it to size
    // that reservation. (The ladder's rung counts + camera rung now live in SdfBenchWorkloads, which builds the
    // battery; this cap stays here — SdfDebugRenderer/SdfDebugMode read it directly.)
    public const int MaxStormInstances = 4096;
    // The articulated-rig stress spans the owner's future range: 12..36 independently animated rigid leaves and five
    // authored VM instructions per leaf (Reset + dynamic + translate + rotate + shape) = 60..180 instructions/avatar.
    // R1 assigns the leaf count without clumping or RNG state; 128 avatars average ~120 instructions and remain below
    // the existing 4096-slot debug reservation. This is deliberately NOT a shared-body instancing model: every avatar
    // owns its own authored instruction ranges and per-leaf cull records.
    public const int MinRigBoneCount = 12;
    public const int MaxRigBoneCount = 36;
    public const int MaxRigAvatars = 128;
    internal const float RigBoundRadius = 1.35f;
    internal const float RigBoneBoundRadius = 0.35f;
    internal const float RigSpacing = 2.25f;
    // The deterministic storm motion (all phases are pure functions of the instance index + the produced-frame counter;
    // no wall clock, no RNG). Amplitudes stay under half the InstanceSpacing so orbiting neighbours never overlap and
    // every copy holds inside the camera's framing (and inside MaxDistance).
    private const float StormOrbitRadius = 0.32f;
    private const float StormBobAmplitude = 0.28f;
    private const float StormOrbitSpeed = 0.11f; // radians of orbit per produced frame
    private const float StormBobSpeed = 0.07f;   // radians of bob phase per produced frame
    private const float StormSpinSpeed = 0.05f;  // radians of per-instance Y spin per produced frame
    private const float StormGoldenAngle = 2.399963f; // π · (3 − √5) — the per-instance phase spread
    // A moving instance's compact per-copy size (a small sphere). The full orbit+bob displacement is baked into the
    // slot's per-frame POSITION, and a dynamic instance's bound center resolves as (slot position + boundOffset), so the
    // bound tracks the mover — the covering radius need only hold the sphere itself (plus float-safety padding), NOT the
    // orbit reach.
    internal const float StormInstanceRadius = 0.28f;
    internal const float StormBoundRadius = (StormInstanceRadius + 0.05f);

    // The carve-bake settle planner for the sdf.carves workload (carve-bake plan §4), settle 0 (IMMEDIATE): a carves
    // config bakes its cluster on the first frame it emits, so the warm window absorbs the bake and the sampled window
    // measures the baked steady state. Shares the process-wide SdfCarveBakePlanner.Enabled gate with the debug pool —
    // off (the default) means every carves rung stays analytic, bit-identical to today (the switch's A/B off leg).
    private readonly SdfCarveBakePlanner m_carvePlanner = new(settleFrames: 0);
    private readonly List<SdfBenchConfig> m_configs = [];
    private readonly List<SdfBenchResult> m_results = [];
    private readonly List<double> m_frameSamples = [];
    private readonly List<double> m_beamSamples = [];
    private readonly List<double> m_viewsSamples = [];
    private readonly List<double> m_compositeSamples = [];
    private Phase m_phase = Phase.Idle;
    private int m_index;
    private int m_framesLeft;
    private int m_warmFrames = 8;
    private int m_sampleFrames = 32;
    private int m_revision;
    private bool m_anyTimingsSeen;
    private string m_runLabel = "";
    // The produced-frame counter the STORM family's deterministic motion + camera sweep read (advanced once per
    // produced frame while a run is in flight — pure frame count, never the wall clock). Reset to 0 at each run's
    // Begin so a run reproduces bit-for-bit.
    private int m_producedFrame;
    // The storm MOTION rung's per-frame dynamic transforms — grown to the active config's instance count and repacked
    // each frame from (index, m_producedFrame). Owned here; the frame source reads it as the frame's DynamicTransforms
    // while a motion config is live (see TryPackStormTransforms).
    private Puck.SdfVm.DynamicTransform[] m_stormTransforms = [];

    // Header facts, refreshed each Advance while running (constant during a run) — the report names them.
    private uint m_width;
    private uint m_height;
    private bool m_backendIsDirectX;

    private enum Phase {
        Idle,
        Warm,
        Sample,
        Done,
    }

    /// <summary>Bumped whenever the active configuration changes — the frame source rebuilds the program when it moves
    /// while the SDF-debug mode is up (mirrors <see cref="SdfDebugScene.Revision"/>).</summary>
    public int Revision => m_revision;

    /// <summary>The carve-bake settle planner for the <see cref="SdfBenchWorkload.Carves"/> workload (settle 0). The
    /// renderer emits through it (<see cref="SdfDebugRenderer.EmitBench"/>); <see cref="AdvanceCarveBake"/> drives it.</summary>
    public SdfCarveBakePlanner CarvePlanner => m_carvePlanner;

    /// <summary>Advances the carve-bake planner one produced frame (carve-bake plan §3/§4): while a
    /// <see cref="SdfBenchWorkload.Carves"/> rung is live it feeds the planner the SAME deterministic carve list the
    /// renderer emits (<see cref="SdfDebugRenderer.BuildBenchCarves"/>), so a settled cluster bakes and adopts; any
    /// other state feeds an empty pool so a prior rung's bricks are released. Returns whether the adopted set changed
    /// (the caller bumps its revision to rebuild).</summary>
    /// <param name="bakes">The engine's brick-bake service.</param>
    /// <returns>Whether the emit plan changed.</returns>
    public bool AdvanceCarveBake(ISdfBrickBakeService bakes) {
        ArgumentNullException.ThrowIfNull(bakes);

        if (Running && (ActiveConfig.Workload == SdfBenchWorkload.Carves)) {
            var carves = SdfDebugRenderer.BuildBenchCarves(family: ActiveConfig.CarveFamily, count: ActiveConfig.InstanceCount);

            return m_carvePlanner.Advance(carves: carves, carveRevision: m_revision, bakes: bakes);
        }

        return m_carvePlanner.Advance(carves: Array.Empty<SdfCarve>(), carveRevision: m_revision, bakes: bakes);
    }

    /// <summary>Whether a run is mid-flight (emitting/warming/sampling). While true the mode renders the bench workload
    /// (not the debug subject), the render node feeds <see cref="Advance"/> each frame, and <see cref="CameraFrame"/>
    /// supplies the fixed pose. False when idle or finished (the debug subject resumes).</summary>
    public bool Running => (m_phase is Phase.Warm or Phase.Sample);

    /// <summary>The warm-up frame count per configuration (default 8).</summary>
    public int WarmFrames => m_warmFrames;

    /// <summary>The measured-sample frame count per configuration (default 32).</summary>
    public int SampleFrames => m_sampleFrames;

    /// <summary>The configuration currently being emitted (valid only while <see cref="Running"/>).</summary>
    public SdfBenchConfig ActiveConfig => m_configs[m_index];

    /// <summary>The deterministic orbit pose framing the active configuration's whole workload, or null when no run is
    /// in flight (the debug controller's pad orbit resumes). Distance is computed per config; yaw/pitch are constant
    /// FOR EVERY workload except the storm CAMERA rung, whose yaw sweeps a full revolution across the sample window
    /// (m_producedFrame-driven, so still deterministic) — every other config returns the byte-identical fixed pose the
    /// ladder tables have always compared against.</summary>
    public (Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite)? CameraFrame {
        get {
            if (!Running) {
                return null;
            }

            var config = ActiveConfig;
            var yaw = OrbitYaw;

            if ((config.Workload == SdfBenchWorkload.Storm) && (config.StormMode == SdfBenchStormMode.Camera)) {
                // One revolution per sample-window's worth of produced frames — a moving VIEW over a still scene, so
                // every sampled frame re-culls at a fresh angle. Deterministic (integer frame count · a fixed step).
                yaw += (m_producedFrame * (MathF.Tau / MathF.Max(x: 1, y: m_sampleFrames)));
            }

            return (Vector3.Zero, yaw, OrbitPitch, ComputeDistance(config: config), false);
        }
    }

    /// <summary>Sets the warm-up frame count (clamped to ≥ 0). Rejected mid-run.</summary>
    /// <returns>A status line.</returns>
    public string SetWarmFrames(int frames) {
        if (Running) {
            return "[sdf.bench.warm: a run is in flight — sdf.bench abort first]";
        }

        m_warmFrames = Math.Max(val1: 0, val2: frames);

        return $"[sdf.bench.warm {m_warmFrames}]";
    }

    /// <summary>Sets the measured-sample frame count (clamped to ≥ 1). Rejected mid-run.</summary>
    /// <returns>A status line.</returns>
    public string SetSampleFrames(int frames) {
        if (Running) {
            return "[sdf.bench.frames: a run is in flight — sdf.bench abort first]";
        }

        m_sampleFrames = Math.Max(val1: 1, val2: frames);

        return $"[sdf.bench.frames {m_sampleFrames}]";
    }

    /// <summary>Aborts an in-flight run (no-op when idle). Bumps the revision so the frame source drops the workload.</summary>
    /// <returns>A status line.</returns>
    public string Abort() {
        if (!Running) {
            return "[sdf.bench.abort: nothing running]";
        }

        var label = m_runLabel;

        m_phase = Phase.Done;
        m_revision++;

        return $"[sdf.bench.abort: cancelled '{label}' at config {(m_index + 1)}/{m_configs.Count}]";
    }

    /// <summary>Starts a <see cref="SdfBenchWorkload.Shapes"/> run — one fullscreen primitive per debuggable shape.
    /// Battery from <see cref="SdfBenchWorkloads.BuildShapesLadder"/>.</summary>
    public string StartShapes() {
        var configs = SdfBenchWorkloads.BuildShapesLadder();

        return Begin(label: "shapes", configs: configs);
    }

    /// <summary>Starts a <see cref="SdfBenchWorkload.Ops"/> run — a fixed torus plus exactly one modifier per row (the
    /// first row is the bare subject), so each row's marginal cost reads against the baseline. Battery from
    /// <see cref="SdfBenchWorkloads.BuildOpsLadder"/>.</summary>
    public string StartOps() {
        var configs = SdfBenchWorkloads.BuildOpsLadder();

        return Begin(label: "ops", configs: configs);
    }

    /// <summary>Starts a single <see cref="SdfBenchWorkload.Instances"/> run — <paramref name="count"/> real instances
    /// of <paramref name="shape"/> in a 3D grid. Config from <see cref="SdfBenchWorkloads.Instances"/>.</summary>
    public string StartInstances(SdfDebugShapeKind shape, int count) {
        var config = SdfBenchWorkloads.Instances(shape: shape, count: count);
        var configs = new List<SdfBenchConfig> { config };

        return Begin(label: $"instances {shape} {config.InstanceCount}", configs: configs);
    }

    /// <summary>Starts one heterogeneous articulated-rig stress rung. Each avatar owns 24 independently animated bone
    /// transforms and 120 authored rigid VM instructions, matching the expected many-limb walking workload rather than
    /// the current demo's toy avatar.</summary>
    public string StartRigs(int count) {
        var config = SdfBenchWorkloads.Rigs(count: count);

        return Begin(label: $"rigs {config.InstanceCount}", configs: [config]);
    }

    /// <summary>Starts a <see cref="SdfBenchWorkload.Instances"/> SWEEP — the default ladder (64/256/1024/4096/16384) of
    /// <paramref name="shape"/>, one config per rung. Battery from
    /// <see cref="SdfBenchWorkloads.BuildInstancesSweepLadder"/>.</summary>
    public string StartSweep(SdfDebugShapeKind shape) {
        var configs = SdfBenchWorkloads.BuildInstancesSweepLadder(shape: shape);

        return Begin(label: $"sweep {shape}", configs: configs);
    }

    /// <summary>Starts a <see cref="SdfBenchWorkload.Carves"/> run — a fixed ~2-unit subject + floor bitten by the carve
    /// ladder (16/64/256/1024) in TWO families (clustered = the honest views-cost worst case; scattered = the beam-wall
    /// control), plus ONE smooth rung (256 clustered SmoothSubtraction carves). Each rung is one table row; the family
    /// is in the label (e.g. <c>carves clustered x256</c>). The subject only shrinks as carves bite it, so the camera
    /// holds the fixed single-shape framing across the whole run. Battery from
    /// <see cref="SdfBenchWorkloads.BuildCarvesLadder"/>.</summary>
    public string StartCarves() {
        var configs = SdfBenchWorkloads.BuildCarvesLadder();

        return Begin(label: "carves", configs: configs);
    }

    /// <summary>Starts the STORM run — the motion/churn ladder. Three families, one battery: the MOTION ladder
    /// (64/256/1024/4096 DYNAMIC instances all moving per frame — the always-list cliff), the REBUILD ladder (the same
    /// counts STATIC but a full program rebuild every frame — the upload/pack ceiling), and one CAMERA rung (a mid-size
    /// static workload under a pose sweeping a full revolution across the sample window). Each rung is one table row; the
    /// mode is in the label (e.g. <c>storm x1024</c>, <c>storm rebuild x1024</c>, <c>storm camera x1024</c>). Battery
    /// from <see cref="SdfBenchWorkloads.BuildStormLadder"/>.</summary>
    public string StartStorm() {
        var configs = SdfBenchWorkloads.BuildStormLadder();

        return Begin(label: "storm", configs: configs);
    }

    /// <summary>Whether the active configuration is a storm MOTION rung, and if so packs this produced frame's dynamic
    /// transforms (grown to the config's instance count) into <paramref name="transforms"/> — the frame source supplies
    /// them as the frame's <c>DynamicTransforms</c> so the moving instances ride the per-frame buffer without a program
    /// rebuild. Every non-motion state (idle, the rebuild/camera rungs, every other workload) returns false, so the room's
    /// own dynamic-transform buffer is used unchanged.</summary>
    public bool TryPackStormTransforms(out Puck.SdfVm.DynamicTransform[] transforms) {
        transforms = [];

        if (!Running) {
            return false;
        }

        var config = ActiveConfig;

        var isStorm = ((config.Workload == SdfBenchWorkload.Storm) && (config.StormMode == SdfBenchStormMode.Motion));
        var isRig = (config.Workload == SdfBenchWorkload.Rigs);

        if (!isStorm && !isRig) {
            return false;
        }

        var avatarCount = Math.Clamp(value: config.InstanceCount, min: 1, max: MaxRigAvatars);
        var n = (isRig ? RigTransformCount(avatarCount: avatarCount) : Math.Clamp(value: config.InstanceCount, min: 1, max: MaxStormInstances));

        if (m_stormTransforms.Length != n) {
            m_stormTransforms = new Puck.SdfVm.DynamicTransform[n];
        }

        var frame = (float)m_producedFrame;

        if (isRig) {
            var slot = 0;

            for (var avatar = 0; (avatar < avatarCount); avatar++) {
                var boneCount = RigBoneCountForAvatar(avatar: avatar);
                var basePosition = RigAvatarPosition(avatar: avatar);
                var phase = (avatar * StormGoldenAngle);
                var orbit = (phase + (frame * StormOrbitSpeed));
                var displaced = new Vector3(
                    x: (basePosition.X + (StormOrbitRadius * MathF.Cos(x: orbit))),
                    y: (basePosition.Y + (StormBobAmplitude * MathF.Sin(x: (phase + (frame * StormBobSpeed))))),
                    z: (basePosition.Z + (StormOrbitRadius * MathF.Sin(x: orbit)))
                );
                var bodyOrientation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (phase + (frame * StormSpinSpeed)));

                for (var bone = 0; (bone < boneCount); bone++, slot++) {
                    var gait = MathF.Sin(x: ((frame * 0.18f) + phase + ((bone & 1) * MathF.PI)));
                    var column = (bone % 6);
                    var limb = ((column <= 1) || (column >= 4));
                    var boneSwing = (0.45f * gait * (limb ? 1f : 0.25f));
                    var orientation = Quaternion.Normalize(value: (Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: boneSwing) * bodyOrientation));
                    var position = (displaced + Vector3.Transform(value: RigBoneOffset(avatar: avatar, bone: bone), rotation: bodyOrientation));

                    m_stormTransforms[slot] = new Puck.SdfVm.DynamicTransform(Position: position, Orientation: orientation);
                }
            }

            transforms = m_stormTransforms;

            return true;
        }

        var grid = GridDimension(count: n);
        var half = (((grid - 1) * InstanceSpacing) * 0.5f);

        for (var index = 0; (index < n); index++) {
            var avatar = index;
            var ix = (avatar % grid);
            var iy = ((avatar / grid) % grid);
            var iz = (avatar / (grid * grid));
            var basePosition = new Vector3(
                x: ((ix * InstanceSpacing) - half),
                y: ((iy * InstanceSpacing) - half),
                z: ((iz * InstanceSpacing) - half)
            );
            var phase = (avatar * StormGoldenAngle);
            var orbit = (phase + (frame * StormOrbitSpeed));
            var displaced = new Vector3(
                x: (basePosition.X + (StormOrbitRadius * MathF.Cos(x: orbit))),
                y: (basePosition.Y + (StormBobAmplitude * MathF.Sin(x: (phase + (frame * StormBobSpeed))))),
                z: (basePosition.Z + (StormOrbitRadius * MathF.Sin(x: orbit)))
            );
            var bodyOrientation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (phase + (frame * StormSpinSpeed)));

            m_stormTransforms[index] = new Puck.SdfVm.DynamicTransform(Position: displaced, Orientation: bodyOrientation);
        }

        transforms = m_stormTransforms;

        return true;
    }

    /// <summary>The worst-case frame budget a run needs, so a script can size its <c>step</c> gates: per config,
    /// warm-up + samples, plus one settle frame each.</summary>
    public int EstimatedFramesFor(int configCount) => (configCount * ((m_warmFrames + m_sampleFrames) + 1));

    /// <summary>Advances the run one produced frame. Called from the render node's produce loop BEFORE this frame's
    /// CaptureFrame, with the PREVIOUS frame's per-pass GPU ms (<paramref name="hasTimings"/> false when timing is off
    /// or no timestamp landed). No-op when idle/finished.</summary>
    public void Advance(bool hasTimings, double beam, double views, double composite, double frame, uint width, uint height, bool backendIsDirectX) {
        if (!Running) {
            return;
        }

        m_width = width;
        m_height = height;
        m_backendIsDirectX = backendIsDirectX;

        // Advance the deterministic frame clock the storm family reads (motion phases + the camera sweep). One tick per
        // produced frame — Advance runs BEFORE this frame's CaptureFrame in the render node's produce loop, so the tick
        // and any rebuild it forces land THIS frame.
        m_producedFrame++;

        // The storm REBUILD rung forces a full program rebuild every produced frame — bump the revision so the frame
        // source's programChanged check fires and BuildProgram + UploadProgram + the packer run again (the measurement).
        // The motion rung rides the dynamic-transform buffer instead (no rebuild); the camera rung rides the pose.
        if ((ActiveConfig.Workload == SdfBenchWorkload.Storm) && (ActiveConfig.StormMode == SdfBenchStormMode.Rebuild)) {
            m_revision++;
        }

        if (m_phase == Phase.Warm) {
            if (--m_framesLeft <= 0) {
                m_phase = Phase.Sample;
                m_framesLeft = m_sampleFrames;

                m_frameSamples.Clear();
                m_beamSamples.Clear();
                m_viewsSamples.Clear();
                m_compositeSamples.Clear();
            }

            return;
        }

        // Sample phase.
        if (hasTimings) {
            m_anyTimingsSeen = true;

            m_frameSamples.Add(item: frame);
            m_beamSamples.Add(item: beam);
            m_viewsSamples.Add(item: views);
            m_compositeSamples.Add(item: composite);
        }

        if (--m_framesLeft > 0) {
            return;
        }

        RecordResult();

        // Timing never came up for the FIRST config — abort the whole run loudly rather than grind through the rest
        // producing zero-sample rows.
        if (!m_anyTimingsSeen && (m_index == 0)) {
            m_phase = Phase.Done;
            m_revision++;

            Console.Out.WriteLine(value: $"[sdf.bench] ABORTED '{m_runLabel}' — no per-pass GPU timings available. Arm GPU timing live (the gpu.timing switch / the world.timing verb) and use a Vulkan/D3D12 host with timestamp support.");

            return;
        }

        m_index++;

        if (m_index >= m_configs.Count) {
            Finish();

            return;
        }

        BeginConfig();
    }

    private string Begin(string label, IReadOnlyList<SdfBenchConfig> configs) {
        if (Running) {
            return $"[sdf.bench: a run ('{m_runLabel}') is already in flight — sdf.bench abort first]";
        }

        if (configs.Count == 0) {
            return "[sdf.bench: nothing to measure]";
        }

        m_configs.Clear();
        m_configs.AddRange(collection: configs);
        m_results.Clear();
        m_index = 0;
        m_producedFrame = 0;
        m_anyTimingsSeen = false;
        m_runLabel = label;

        BeginConfig();

        Console.Out.WriteLine(value: $"[sdf.bench] START '{label}' — {configs.Count} config(s), warm={m_warmFrames} samples={m_sampleFrames} (~{EstimatedFramesFor(configCount: configs.Count)} frames). Progress + table to stdout.");

        return $"[sdf.bench {label}] started — {configs.Count} config(s); ~{EstimatedFramesFor(configCount: configs.Count)} frames. Watch stdout for the table (or step past it).";
    }

    // Selects config m_index: bump the revision (the frame source rebuilds the program to this workload on the frame
    // that follows) and reset the warm-up countdown.
    private void BeginConfig() {
        m_phase = Phase.Warm;
        m_framesLeft = Math.Max(val1: 1, val2: m_warmFrames);
        m_revision++;

        var config = m_configs[m_index];

        Console.Out.WriteLine(value: $"[sdf.bench] {(m_index + 1)}/{m_configs.Count} {config.Label} — warming {m_warmFrames}, sampling {m_sampleFrames}");
    }
    private void RecordResult() {
        var config = m_configs[m_index];
        var has = (m_frameSamples.Count > 0);

        m_results.Add(item: new SdfBenchResult(
            Label: config.Label,
            InstanceCount: config.InstanceCount,
            HasTimings: has,
            FrameMed: Median(values: m_frameSamples), FrameMin: Min(values: m_frameSamples), FrameMax: Max(values: m_frameSamples),
            BeamMed: Median(values: m_beamSamples), BeamMin: Min(values: m_beamSamples), BeamMax: Max(values: m_beamSamples),
            ViewsMed: Median(values: m_viewsSamples), ViewsMin: Min(values: m_viewsSamples), ViewsMax: Max(values: m_viewsSamples),
            CompositeMed: Median(values: m_compositeSamples), CompositeMin: Min(values: m_compositeSamples), CompositeMax: Max(values: m_compositeSamples)
        ));
    }
    private void Finish() {
        m_phase = Phase.Done;
        m_revision++;

        Console.Out.WriteLine(value: FormatReport());
        Console.Out.WriteLine(value: $"[sdf.bench] DONE '{m_runLabel}'");
    }

    /// <summary>Renders the completed run as a fixed-width table: a header naming resolution/backend/warm-up/sample
    /// counts, then one row per configuration (config | frame med (min-max) | beam med | views med | composite med).</summary>
    public string FormatReport() {
        var backend = (m_backendIsDirectX ? "Direct3D 12" : "Vulkan");
        var builder = new StringBuilder();

        builder.Append(value: "[sdf.bench] ").Append(value: m_runLabel).Append(value: "  |  ")
            .Append(value: m_width).Append(value: 'x').Append(value: m_height).Append(value: "  |  ")
            .Append(value: backend).Append(value: "  |  warm=").Append(value: m_warmFrames)
            .Append(value: " samples=").Append(value: m_sampleFrames).Append(value: "  |  all times ms").Append(value: '\n');

        builder.Append(value: Row(config: "config", frame: "frame med (min-max)", beam: "beam", views: "views", composite: "composite")).Append(value: '\n');
        builder.Append(value: new string(c: '-', count: 82)).Append(value: '\n');

        foreach (var result in m_results) {
            if (!result.HasTimings) {
                builder.Append(value: Row(config: result.Label, frame: "(no timings — GPU timing not armed?)", beam: "-", views: "-", composite: "-")).Append(value: '\n');

                continue;
            }

            var frame = string.Create(provider: CultureInfo.InvariantCulture, handler: $"{result.FrameMed,7:F3} ({result.FrameMin,7:F3}-{result.FrameMax,7:F3})");

            builder.Append(value: Row(
                config: result.Label,
                frame: frame,
                beam: result.BeamMed.ToString(format: "F3", provider: CultureInfo.InvariantCulture),
                views: result.ViewsMed.ToString(format: "F3", provider: CultureInfo.InvariantCulture),
                composite: result.CompositeMed.ToString(format: "F3", provider: CultureInfo.InvariantCulture)
            )).Append(value: '\n');
        }

        return builder.ToString();
    }

    private static string Row(string config, string frame, string beam, string views, string composite) =>
        string.Create(provider: CultureInfo.InvariantCulture, handler: $"  {config,-18} | {frame,-26} | {beam,9} | {views,9} | {composite,9}");

    // The orbit distance that frames the config's whole workload inside the FOV, with slack and a floor. For a single
    // fullscreen shape (shapes/ops) a fixed distance keeps the camera still; for an instance grid it scales with the
    // grid's half-extent so every copy stays in frame and inside the march budget.
    private static float ComputeDistance(SdfBenchConfig config) {
        // The instance-grid workloads (Instances + every Storm rung) frame the whole grid; a single fullscreen subject
        // (shapes/ops/carves) holds a fixed distance so the camera never reframes across the run.
        if ((config.Workload != SdfBenchWorkload.Instances) && (config.Workload != SdfBenchWorkload.Rigs) && (config.Workload != SdfBenchWorkload.Storm)) {
            return SingleShapeDistance;
        }

        float workloadRadius;

        if (config.Workload == SdfBenchWorkload.Rigs) {
            var lastAvatar = Math.Max(val1: 0, val2: (Math.Min(val1: config.InstanceCount, val2: MaxRigAvatars) - 1));
            var ring = LayerSequence.CenteredHexagonal.LayerOf(index: lastAvatar);

            workloadRadius = ((ring * RigSpacing) + RigBoundRadius);
        } else {
            var grid = GridDimension(count: config.InstanceCount);
            var halfExtent = (((grid - 1) * InstanceSpacing) * 0.5f);

            workloadRadius = (halfExtent + InstanceBoundRadius);
        }

        var distance = ((workloadRadius / MathF.Tan(x: (FieldOfViewRadians * 0.5f))) * FrameMargin);

        return MathF.Max(x: MinCameraDistance, y: distance);
    }

    /// <summary>The per-axis cell count of the smallest cube grid holding <paramref name="count"/> instances
    /// (⌈∛count⌉).</summary>
    internal static int GridDimension(int count) => Math.Max(val1: 1, val2: (int)MathF.Ceiling(x: MathF.Cbrt(x: MathF.Max(x: 1f, y: count))));

    internal static int RigBoneCountForAvatar(int avatar) {
        var fraction = LowDiscrepancy.R1(index: (ulong)Math.Max(val1: 0, val2: avatar));
        var span = ((MaxRigBoneCount - MinRigBoneCount) + 1);

        return (MinRigBoneCount + (int)(((ulong)fraction.Value * (uint)span) >> 32));
    }

    internal static int RigTransformCount(int avatarCount) {
        var total = 0;

        for (var avatar = 0; (avatar < Math.Clamp(value: avatarCount, min: 0, max: MaxRigAvatars)); avatar++) {
            total += RigBoneCountForAvatar(avatar: avatar);
        }

        return total;
    }

    // Maps the centered-hexagonal layer/offset to an axial hex coordinate, then to the XZ ground plane. The layer
    // lookup is Puck.Maths' constant-time integer inverse; no cube-root grid, mutable ring walk, or RNG is involved.
    internal static Vector3 RigAvatarPosition(int avatar) {
        var location = LayerSequence.CenteredHexagonal.Locate(index: Math.Max(val1: 0, val2: avatar));

        if (location.Layer == 0) {
            return Vector3.Zero;
        }

        var radius = (int)location.Layer;
        var side = ((int)location.Offset / radius);
        var step = ((int)location.Offset % radius);
        var axial = side switch {
            0 => (Q: radius, R: -step),
            1 => (Q: (radius - step), R: -radius),
            2 => (Q: -step, R: (-radius + step)),
            3 => (Q: -radius, R: step),
            4 => (Q: (-radius + step), R: radius),
            _ => (Q: step, R: (radius - step)),
        };

        return new Vector3(
            x: (RigSpacing * (axial.Q + (0.5f * axial.R))),
            y: 0f,
            z: (RigSpacing * 0.8660254f * axial.R)
        );
    }

    internal static Vector3 RigBoneOffset(int avatar, int bone) {
        var column = (bone % 6);
        var band = (bone / 6);
        var variation = LowDiscrepancy.R2(index: (ulong)(((avatar * MaxRigBoneCount) + bone) + 1));

        return new Vector3(
            x: ((column - 2.5f) * 0.105f),
            y: (-0.70f + (0.28f * band)),
            z: ((((float)(double)variation.Y) - 0.5f) * 0.12f)
        );
    }

    private static double Median(List<double> values) {
        if (values.Count == 0) {
            return 0.0;
        }

        var sorted = values.ToArray();

        Array.Sort(array: sorted);

        var mid = (sorted.Length / 2);

        return (((sorted.Length & 1) == 1) ? sorted[mid] : ((sorted[(mid - 1)] + sorted[mid]) * 0.5));
    }
    private static double Min(List<double> values) {
        if (values.Count == 0) {
            return 0.0;
        }

        var min = values[0];

        foreach (var value in values) {
            min = Math.Min(val1: min, val2: value);
        }

        return min;
    }
    private static double Max(List<double> values) {
        if (values.Count == 0) {
            return 0.0;
        }

        var max = values[0];

        foreach (var value in values) {
            max = Math.Max(val1: max, val2: value);
        }

        return max;
    }
}
