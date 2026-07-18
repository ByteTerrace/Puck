namespace Puck.SdfVm.Debug;

/// <summary>The synthetic-ladder workload program builders shared between <see cref="SdfBenchScene"/>'s
/// <c>sdf.bench</c> battery and any other caller that wants ONE named workload at ONE explicit rung — e.g. a demo
/// bench-scene adapter that swaps a single program into the SDF debug engine and lets an outside harness sample
/// through the ordinary per-pass timing seams, rather than running the whole ladder. Every method here is a PURE
/// <see cref="SdfBenchConfig"/> builder — it describes what to emit; the actual GPU-program emission lives in
/// <see cref="SdfDebugRenderer.EmitBench"/>. Extracted verbatim from <see cref="SdfBenchScene"/> so the ladder's
/// construction constants and labels stay byte-identical — <c>sdf.bench</c>'s behavior does not change.</summary>
public static class SdfBenchWorkloads {
    // The default INSTANCES sweep ladder.
    private static readonly int[] DefaultInstancesSweep = [64, 256, 1024, 4096, 16384];

    // The CARVES ladder (per family). Tops out at 4096 = SdfDebugScene.MaxCarves (the live pool cap), so the bench
    // and the live subject share a ceiling.
    private static readonly int[] CarveLadder = [16, 64, 256, 1024, 4096];
    // The single smooth-carve rung — 256 clustered SmoothSubtraction carves (halo × mask-width pressure).
    private const int SmoothCarveRung = 256;

    // The STORM ladder (the motion/churn family). Tops out at SdfBenchScene.MaxStormInstances (the dynamic-transform
    // capacity floor the render assembly reserves for the mode).
    private static readonly int[] StormLadder = [64, 256, 1024, 4096];
    // The single fast-camera rung's static count (mid-size — enough to make the re-cull cost read, small enough to
    // stay well framed while the pose sweeps a full revolution).
    private const int StormCameraRung = 1024;

    /// <summary>Builds the SHAPES ladder — one config per catalogued <see cref="SdfDebugShapeKind"/> (fullscreen, no
    /// modifier). This IS <c>sdf.bench shapes</c>' battery.</summary>
    public static IReadOnlyList<SdfBenchConfig> BuildShapesLadder() {
        var configs = new List<SdfBenchConfig>();

        foreach (var kind in Enum.GetValues<SdfDebugShapeKind>()) {
            configs.Add(item: Shape(shape: kind));
        }

        return configs;
    }

    /// <summary>A single SHAPES config for one <paramref name="shape"/> (fullscreen, no modifier) — the named
    /// workload a caller (e.g. a demo bench-scene adapter) swaps in directly at ONE explicit selection, without
    /// running the whole ladder.</summary>
    public static SdfBenchConfig Shape(SdfDebugShapeKind shape) =>
        new(Label: shape.ToString(), Workload: SdfBenchWorkload.Shapes, Shape: shape, Op: SdfBenchOp.Baseline, InstanceCount: 0);

    /// <summary>Builds the OPS ladder — a fixed torus plus exactly one modifier per row (the first row is the bare
    /// subject), so each row's marginal cost reads against the baseline. This IS <c>sdf.bench ops</c>' battery.</summary>
    public static IReadOnlyList<SdfBenchConfig> BuildOpsLadder() {
        var configs = new List<SdfBenchConfig>();

        foreach (var op in Enum.GetValues<SdfBenchOp>()) {
            configs.Add(item: Op(op: op));
        }

        return configs;
    }

    /// <summary>A single OPS config for one <paramref name="op"/> against the fixed torus subject (the label reads
    /// "baseline (torus)" for <see cref="SdfBenchOp.Baseline"/>, the op name otherwise).</summary>
    public static SdfBenchConfig Op(SdfBenchOp op) {
        var label = ((op == SdfBenchOp.Baseline) ? "baseline (torus)" : op.ToString());

        return new SdfBenchConfig(Label: label, Workload: SdfBenchWorkload.Ops, Shape: SdfDebugShapeKind.Torus, Op: op, InstanceCount: 0);
    }

    /// <summary>A single INSTANCES config — <paramref name="count"/> real instances of <paramref name="shape"/> in a
    /// 3D grid, clamped to [1, <see cref="SdfVm.SdfProgramBuilder.MaxInstances"/>]. This IS the rung a caller (e.g. a
    /// demo bench-scene adapter) requests directly (the 1024 rung).</summary>
    public static SdfBenchConfig Instances(SdfDebugShapeKind shape, int count) {
        var n = Math.Clamp(value: count, min: 1, max: SdfVm.SdfProgramBuilder.MaxInstances);

        return new SdfBenchConfig(Label: $"{shape} x{n}", Workload: SdfBenchWorkload.Instances, Shape: shape, Op: SdfBenchOp.Baseline, InstanceCount: n);
    }

    /// <summary>A heterogeneous articulated-rig stress config. Each avatar emits 24 animated rigid leaves / 120 VM
    /// instructions; the count is clamped by the debug engine's existing 4096 dynamic-slot reservation.</summary>
    public static SdfBenchConfig Rigs(int count) {
        var n = Math.Clamp(value: count, min: 1, max: SdfBenchScene.MaxRigAvatars);

        return new SdfBenchConfig(Label: $"rigs x{n}", Workload: SdfBenchWorkload.Rigs, Shape: SdfDebugShapeKind.Box, Op: SdfBenchOp.Baseline, InstanceCount: n);
    }

    /// <summary>Builds the INSTANCES SWEEP ladder — the default ladder (64/256/1024/4096/16384) of
    /// <paramref name="shape"/>, one config per rung. This IS <c>sdf.bench sweep</c>'s battery.</summary>
    public static IReadOnlyList<SdfBenchConfig> BuildInstancesSweepLadder(SdfDebugShapeKind shape) {
        var configs = new List<SdfBenchConfig>();

        foreach (var n in DefaultInstancesSweep) {
            configs.Add(item: Instances(shape: shape, count: n));
        }

        return configs;
    }

    /// <summary>A single CARVES config — <paramref name="count"/> carves of <paramref name="family"/> against the
    /// fixed sphere subject. This IS the rung a caller (e.g. a demo bench-scene adapter) requests directly (the 1024
    /// rung, clustered family).</summary>
    public static SdfBenchConfig Carve(SdfBenchCarveFamily family, int count) =>
        new(Label: $"carves {family.ToString().ToLowerInvariant()} x{count}", Workload: SdfBenchWorkload.Carves, Shape: SdfDebugShapeKind.Sphere, Op: SdfBenchOp.Baseline, InstanceCount: count, CarveFamily: family);

    /// <summary>Builds the CARVES ladder — a fixed ~2-unit subject + floor bitten by the carve ladder (16/64/256/
    /// 1024/4096) in TWO families (clustered = the honest views-cost worst case; scattered = the beam-wall control),
    /// plus ONE smooth rung (256 clustered SmoothSubtraction carves). This IS <c>sdf.bench carves</c>'s battery.</summary>
    public static IReadOnlyList<SdfBenchConfig> BuildCarvesLadder() {
        var configs = new List<SdfBenchConfig>();

        foreach (var n in CarveLadder) {
            configs.Add(item: Carve(family: SdfBenchCarveFamily.Clustered, count: n));
        }

        foreach (var n in CarveLadder) {
            configs.Add(item: Carve(family: SdfBenchCarveFamily.Scattered, count: n));
        }

        configs.Add(item: Carve(family: SdfBenchCarveFamily.Smooth, count: SmoothCarveRung));

        return configs;
    }

    /// <summary>A single STORM config for one <paramref name="mode"/> at <paramref name="count"/> instances — the
    /// label mirrors the ladder's own (<c>storm x{n}</c>, <c>storm rebuild x{n}</c>, <c>storm camera x{n}</c>). This
    /// IS the rung a caller (e.g. a demo bench-scene adapter) requests directly (the 1024 rung).</summary>
    public static SdfBenchConfig StormRung(SdfBenchStormMode mode, int count) {
        var suffix = mode switch {
            SdfBenchStormMode.Rebuild => " rebuild",
            SdfBenchStormMode.Camera => " camera",
            _ => "",
        };

        return new SdfBenchConfig(Label: $"storm{suffix} x{count}", Workload: SdfBenchWorkload.Storm, Shape: SdfDebugShapeKind.Sphere, Op: SdfBenchOp.Baseline, InstanceCount: count, StormMode: mode);
    }

    /// <summary>Builds the STORM ladder — the motion/churn ladder. Three families, one battery: the MOTION ladder
    /// (64/256/1024/4096 DYNAMIC instances all moving per frame — the always-list cliff), the REBUILD ladder (the
    /// same counts STATIC but a full program rebuild every frame — the upload/pack ceiling), and one CAMERA rung (a
    /// mid-size static workload under a pose sweeping a full revolution across the sample window). This IS
    /// <c>sdf.bench storm</c>'s battery.</summary>
    public static IReadOnlyList<SdfBenchConfig> BuildStormLadder() {
        var configs = new List<SdfBenchConfig>();

        foreach (var n in StormLadder) {
            configs.Add(item: StormRung(mode: SdfBenchStormMode.Motion, count: n));
        }

        foreach (var n in StormLadder) {
            configs.Add(item: StormRung(mode: SdfBenchStormMode.Rebuild, count: n));
        }

        configs.Add(item: StormRung(mode: SdfBenchStormMode.Camera, count: StormCameraRung));

        return configs;
    }
}
