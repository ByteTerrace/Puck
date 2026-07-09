using System.Globalization;
using System.Numerics;
using System.Text;

namespace Puck.Demo.SdfDebug;

/// <summary>The workload family a bench run measures — <see cref="Shapes"/> (each debuggable primitive fullscreen),
/// <see cref="Ops"/> (a fixed torus + one modifier, marginal-cost vs the bare subject), and <see cref="Instances"/>
/// (a 3D grid of N real instances of one shape).</summary>
public enum SdfBenchWorkload {
    Shapes,
    Ops,
    Instances,
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
/// three workloads need (each reads only its own). A run is an ordered list of these; the runner emits, warms, and
/// samples each in turn.</summary>
public readonly record struct SdfBenchConfig(string Label, SdfBenchWorkload Workload, SdfDebugShapeKind Shape, SdfBenchOp Op, int InstanceCount);

/// <summary>One configuration's measured timing: the median plus min/max of each per-pass GPU-ms channel over the
/// sample window. <see cref="HasTimings"/> is false when the window collected no timing samples (PUCK_TIMING off or no
/// timestamps) — the report says so loudly instead of printing zeros.</summary>
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
/// into <see cref="SdfDebugMode"/> beside the scene). It cannot block <c>CaptureFrame</c>, so it advances ONE step per
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

    // The default sweep ladder.
    private static readonly int[] DefaultSweep = [64, 256, 1024, 4096];

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

    /// <summary>The fixed deterministic orbit pose framing the active configuration's whole workload, or null when no
    /// run is in flight (the debug controller's pad orbit resumes). Distance is computed per config; yaw/pitch are
    /// constant.</summary>
    public (Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite)? CameraFrame =>
        (Running ? (Vector3.Zero, OrbitYaw, OrbitPitch, ComputeDistance(config: ActiveConfig), false) : null);

    /// <summary>Sets the warm-up frame count (clamped to ≥ 0). Rejected mid-run.</summary>
    /// <returns>A status line.</returns>
    public string SetWarmFrames(int frames) {
        if (Running) {
            return "[sdf.bench.warm: a run is in flight — sdf.bench abort first]";
        }

        m_warmFrames = Math.Max(0, frames);

        return $"[sdf.bench.warm {m_warmFrames}]";
    }

    /// <summary>Sets the measured-sample frame count (clamped to ≥ 1). Rejected mid-run.</summary>
    /// <returns>A status line.</returns>
    public string SetSampleFrames(int frames) {
        if (Running) {
            return "[sdf.bench.frames: a run is in flight — sdf.bench abort first]";
        }

        m_sampleFrames = Math.Max(1, frames);

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

        return $"[sdf.bench.abort: cancelled '{label}' at config {m_index + 1}/{m_configs.Count}]";
    }

    /// <summary>Starts a <see cref="SdfBenchWorkload.Shapes"/> run — one fullscreen primitive per debuggable shape.</summary>
    public string StartShapes() {
        var configs = new List<SdfBenchConfig>();

        foreach (var kind in Enum.GetValues<SdfDebugShapeKind>()) {
            configs.Add(item: new SdfBenchConfig(Label: kind.ToString(), Workload: SdfBenchWorkload.Shapes, Shape: kind, Op: SdfBenchOp.Baseline, InstanceCount: 0));
        }

        return Begin(label: "shapes", configs: configs);
    }

    /// <summary>Starts a <see cref="SdfBenchWorkload.Ops"/> run — a fixed torus plus exactly one modifier per row (the
    /// first row is the bare subject), so each row's marginal cost reads against the baseline.</summary>
    public string StartOps() {
        var configs = new List<SdfBenchConfig>();

        foreach (var op in Enum.GetValues<SdfBenchOp>()) {
            var label = ((op == SdfBenchOp.Baseline) ? "baseline (torus)" : op.ToString());

            configs.Add(item: new SdfBenchConfig(Label: label, Workload: SdfBenchWorkload.Ops, Shape: SdfDebugShapeKind.Torus, Op: op, InstanceCount: 0));
        }

        return Begin(label: "ops", configs: configs);
    }

    /// <summary>Starts a single <see cref="SdfBenchWorkload.Instances"/> run — <paramref name="count"/> real instances
    /// of <paramref name="shape"/> in a 3D grid.</summary>
    public string StartInstances(SdfDebugShapeKind shape, int count) {
        var n = Math.Clamp(value: count, min: 1, max: SdfVm.SdfProgramBuilder.MaxInstances);
        var configs = new List<SdfBenchConfig> {
            new(Label: $"{shape} x{n}", Workload: SdfBenchWorkload.Instances, Shape: shape, Op: SdfBenchOp.Baseline, InstanceCount: n),
        };

        return Begin(label: $"instances {shape} {n}", configs: configs);
    }

    /// <summary>Starts a <see cref="SdfBenchWorkload.Instances"/> SWEEP — the default ladder (64/256/1024/4096) of
    /// <paramref name="shape"/>, one config per rung.</summary>
    public string StartSweep(SdfDebugShapeKind shape) {
        var configs = new List<SdfBenchConfig>();

        foreach (var n in DefaultSweep) {
            configs.Add(item: new SdfBenchConfig(Label: $"{shape} x{n}", Workload: SdfBenchWorkload.Instances, Shape: shape, Op: SdfBenchOp.Baseline, InstanceCount: n));
        }

        return Begin(label: $"sweep {shape}", configs: configs);
    }

    /// <summary>The worst-case frame budget a run needs, so a script can size its <c>step</c> gates: per config,
    /// warm-up + samples, plus one settle frame each.</summary>
    public int EstimatedFramesFor(int configCount) => (configCount * (m_warmFrames + m_sampleFrames + 1));

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

            Console.Out.WriteLine(value: $"[sdf.bench] ABORTED '{m_runLabel}' — no per-pass GPU timings available. Set PUCK_TIMING=1 (and use a Vulkan/D3D12 host with timestamp support).");

            return;
        }

        m_index++;

        if (m_index >= m_configs.Count) {
            Finish();

            return;
        }

        BeginConfig();
    }

    private string Begin(string label, List<SdfBenchConfig> configs) {
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
        m_framesLeft = Math.Max(1, m_warmFrames);
        m_revision++;

        var config = m_configs[m_index];

        Console.Out.WriteLine(value: $"[sdf.bench] {m_index + 1}/{m_configs.Count} {config.Label} — warming {m_warmFrames}, sampling {m_sampleFrames}");
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
                builder.Append(value: Row(config: result.Label, frame: "(no timings — PUCK_TIMING off?)", beam: "-", views: "-", composite: "-")).Append(value: '\n');

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
        if (config.Workload != SdfBenchWorkload.Instances) {
            return SingleShapeDistance;
        }

        var grid = GridDimension(count: config.InstanceCount);
        var halfExtent = (((grid - 1) * InstanceSpacing) * 0.5f);
        var workloadRadius = (halfExtent + InstanceBoundRadius);
        var distance = ((workloadRadius / MathF.Tan(x: (FieldOfViewRadians * 0.5f))) * FrameMargin);

        return MathF.Max(MinCameraDistance, distance);
    }

    /// <summary>The per-axis cell count of the smallest cube grid holding <paramref name="count"/> instances
    /// (⌈∛count⌉).</summary>
    internal static int GridDimension(int count) => Math.Max(1, (int)MathF.Ceiling(x: MathF.Cbrt(x: MathF.Max(1f, count))));

    private static double Median(List<double> values) {
        if (values.Count == 0) {
            return 0.0;
        }

        var sorted = values.ToArray();

        Array.Sort(array: sorted);

        var mid = (sorted.Length / 2);

        return (((sorted.Length & 1) == 1) ? sorted[mid] : ((sorted[mid - 1] + sorted[mid]) * 0.5));
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
