using Puck.SignedDistance;

namespace Puck.SdfVm.Debug;

/// <summary>The synthetic-ladder workload program builders shared between <see cref="SdfBenchScene"/>'s
/// <c>sdf.bench</c> battery and any other caller that wants one named workload at one explicit rung — e.g. a demo
/// bench-scene adapter that swaps a single program into the SDF debug engine and lets an outside harness sample
/// through the ordinary per-pass timing seams, rather than running the whole ladder. Every method here is a pure
/// <see cref="SdfBenchConfig"/> builder — it describes what to emit; the actual GPU-program emission lives in
/// <see cref="SdfDebugRenderer.EmitBench"/>. Extracted verbatim from <see cref="SdfBenchScene"/> so the ladder's
/// construction constants and labels stay byte-identical — <c>sdf.bench</c>'s behavior does not change.</summary>
public static class SdfBenchWorkloads {
    // The single smooth-carve rung — 256 clustered SmoothSubtraction carves (halo × mask-width pressure).
    private const int SmoothCarveRung = 256;
    // The single fast-camera rung's static count (mid-size — enough to make the re-cull cost read, small enough to
    // stay well framed while the pose sweeps a full revolution).
    private const int StormCameraRung = 1024;

    // The CARVES ladder (per family). Tops out at 4096 = SdfDebugScene.MaxCarves (the live pool cap), so the bench
    // and the live subject share a ceiling.
    private static readonly int[] CarveLadder = [16, 64, 256, 1024, 4096];
    // The default INSTANCES sweep ladder.
    private static readonly int[] DefaultInstancesSweep = [64, 256, 1024, 4096, 16384];
    // The DYNAMIC MATRIX ladder: N=0 is the per-cell baseline control, the rest is the requested N sweep. Tops out
    // at SdfProgramBuilder.MaxInstances (16384) — unlike Storm's motion rungs (capped at
    // MaxStormInstances=4096), DynamicMatrix measures to the full instance cap, moving or static.
    private static readonly int[] DynamicMatrixLadder = [0, 256, 1024, 4096, 16384];
    // The STORM ladder (the motion/churn family). Tops out at SdfBenchScene.MaxStormInstances (the dynamic-transform
    // capacity floor the render assembly reserves for the mode).
    private static readonly int[] StormLadder = [64, 256, 1024, 4096];

    /// <summary>Builds the carves ladder — a fixed ~2-unit subject + floor bitten by the carve ladder (16/64/256/
    /// 1024/4096) in two families (clustered = the honest views-cost worst case; scattered = the beam-wall control),
    /// plus one smooth rung (256 clustered SmoothSubtraction carves). This is <c>sdf.bench carves</c>'s battery.</summary>
    public static IReadOnlyList<SdfBenchConfig> BuildCarvesLadder() {
        var configs = new List<SdfBenchConfig>();

        foreach (var n in CarveLadder) {
            configs.Add(item: Carve(
                count: n,
                family: SdfBenchCarveFamily.Clustered
            ));
        }

        foreach (var n in CarveLadder) {
            configs.Add(item: Carve(
                count: n,
                family: SdfBenchCarveFamily.Scattered
            ));
        }

        configs.Add(item: Carve(
            count: SmoothCarveRung,
            family: SdfBenchCarveFamily.Smooth
        ));

        return configs;
    }
    /// <summary>Builds the dynamic matrix ladder — N ∈ {0, 256, 1024, 4096, 16384} × <see cref="SdfBenchPlacement"/>
    /// {Clustered, Uniform, FarCorners} × {static, moving}, one config per cell (30 rows). The N=0 rung of every
    /// (placement, moving) pair is that cell's baseline control. This is <c>sdf.bench matrix</c>'s battery.</summary>
    public static IReadOnlyList<SdfBenchConfig> BuildDynamicMatrixLadder() {
        var configs = new List<SdfBenchConfig>();

        foreach (var placement in Enum.GetValues<SdfBenchPlacement>()) {
            foreach (var moving in new[] { false, true }) {
                foreach (var n in DynamicMatrixLadder) {
                    configs.Add(item: DynamicMatrixRung(
                        count: n,
                        moving: moving,
                        placement: placement
                    ));
                }
            }
        }

        return configs;
    }
    /// <summary>Builds the instances sweep ladder — the default ladder (64/256/1024/4096/16384) of
    /// <paramref name="shape"/>, one config per rung. This is <c>sdf.bench sweep</c>'s battery.</summary>
    public static IReadOnlyList<SdfBenchConfig> BuildInstancesSweepLadder(SdfDebugShapeKind shape) {
        var configs = new List<SdfBenchConfig>();

        foreach (var n in DefaultInstancesSweep) {
            configs.Add(item: Instances(
                count: n,
                shape: shape
            ));
        }

        return configs;
    }
    /// <summary>Builds the ops ladder — a fixed torus plus exactly one modifier per row (the first row is the bare
    /// subject), so each row's marginal cost reads against the baseline. This is <c>sdf.bench ops</c>' battery.</summary>
    public static IReadOnlyList<SdfBenchConfig> BuildOpsLadder() {
        var configs = new List<SdfBenchConfig>();

        foreach (var op in Enum.GetValues<SdfBenchOp>()) {
            configs.Add(item: Op(op: op));
        }

        return configs;
    }
    /// <summary>Builds the shapes ladder — one config per catalogued <see cref="SdfDebugShapeKind"/> (fullscreen, no
    /// modifier). This is <c>sdf.bench shapes</c>' battery.</summary>
    public static IReadOnlyList<SdfBenchConfig> BuildShapesLadder() {
        var configs = new List<SdfBenchConfig>();

        foreach (var kind in Enum.GetValues<SdfDebugShapeKind>()) {
            configs.Add(item: Shape(shape: kind));
        }

        return configs;
    }
    /// <summary>Builds the storm ladder — the motion/churn ladder. Three families, one battery: the motion ladder
    /// (64/256/1024/4096 dynamic instances all moving per frame — the always-list cliff), the rebuild ladder (the
    /// same counts static but a full program rebuild every frame — the upload/pack ceiling), and one camera rung (a
    /// mid-size static workload under a pose sweeping a full revolution across the sample window). This is
    /// <c>sdf.bench storm</c>'s battery.</summary>
    public static IReadOnlyList<SdfBenchConfig> BuildStormLadder() {
        var configs = new List<SdfBenchConfig>();

        foreach (var n in StormLadder) {
            configs.Add(item: StormRung(
                count: n,
                mode: SdfBenchStormMode.Motion
            ));
        }

        foreach (var n in StormLadder) {
            configs.Add(item: StormRung(
                count: n,
                mode: SdfBenchStormMode.Rebuild
            ));
        }

        configs.Add(item: StormRung(
            count: StormCameraRung,
            mode: SdfBenchStormMode.Camera
        ));

        return configs;
    }
    /// <summary>A single carves config — <paramref name="count"/> carves of <paramref name="family"/> against the
    /// fixed sphere subject. This is the rung a caller (e.g. a demo bench-scene adapter) requests directly (the 1024
    /// rung, clustered family).</summary>
    public static SdfBenchConfig Carve(SdfBenchCarveFamily family, int count) =>
        new(
            Label: $"carves {family.ToString().ToLowerInvariant()} x{count}",
            Workload: SdfBenchWorkload.Carves,
            Shape: SdfDebugShapeKind.Sphere,
            Op: SdfBenchOp.Baseline,
            InstanceCount: count,
            CarveFamily: family
        );
    /// <summary>A single dynamic matrix config — <paramref name="count"/> spheres placed by <paramref name="placement"/>,
    /// either baked static (grid-invariant, no per-frame CPU rebuild) or moving (dynamic slots orbiting per produced
    /// frame — forces the per-frame instance-grid rebuild). This is the rung a caller (e.g. the ceiling-measurement
    /// harness) requests directly.</summary>
    public static SdfBenchConfig DynamicMatrixRung(SdfBenchPlacement placement, bool moving, int count) {
        var n = Math.Clamp(
            max: SdfProgramBuilder.MaxInstances,
            min: 0,
            value: count
        );
        var placementToken = placement switch {
            SdfBenchPlacement.Clustered => "clustered",
            SdfBenchPlacement.FarCorners => "far-corners",
            _ => "uniform",
        };
        var movingToken = (moving
            ? "moving"
            : "static"
        );

        return new SdfBenchConfig(
            Label: $"matrix {placementToken} {movingToken} x{n}",
            Workload: SdfBenchWorkload.DynamicMatrix,
            Shape: SdfDebugShapeKind.Sphere,
            Op: SdfBenchOp.Baseline,
            InstanceCount: n,
            Placement: placement,
            Moving: moving
        );
    }
    /// <summary>A single instances config — <paramref name="count"/> real instances of <paramref name="shape"/> in a
    /// 3D grid, clamped to [1, <see cref="SdfProgramBuilder.MaxInstances"/>]. This is the rung a caller (e.g. a
    /// demo bench-scene adapter) requests directly (the 1024 rung).</summary>
    public static SdfBenchConfig Instances(SdfDebugShapeKind shape, int count) {
        var n = Math.Clamp(
            max: SdfProgramBuilder.MaxInstances,
            min: 1,
            value: count
        );

        return new SdfBenchConfig(
            Label: $"{shape} x{n}",
            Workload: SdfBenchWorkload.Instances,
            Shape: shape,
            Op: SdfBenchOp.Baseline,
            InstanceCount: n
        );
    }
    /// <summary>A single ops config for one <paramref name="op"/> against the fixed torus subject (the label reads
    /// "baseline (torus)" for <see cref="SdfBenchOp.Baseline"/>, the op name otherwise).</summary>
    public static SdfBenchConfig Op(SdfBenchOp op) {
        var label = ((op == SdfBenchOp.Baseline)
            ? "baseline (torus)"
            : op.ToString()
        );

        return new SdfBenchConfig(
            Label: label,
            Workload: SdfBenchWorkload.Ops,
            Shape: SdfDebugShapeKind.Torus,
            Op: op,
            InstanceCount: 0
        );
    }
    /// <summary>A heterogeneous articulated-rig stress config. Each avatar emits 24 animated rigid leaves / 120 VM
    /// instructions; the count is clamped by the debug engine's existing 4096 dynamic-slot reservation.</summary>
    public static SdfBenchConfig Rigs(int count) {
        var n = Math.Clamp(
            max: SdfBenchScene.MaxRigAvatars,
            min: 1,
            value: count
        );

        return new SdfBenchConfig(
            Label: $"rigs x{n}",
            Workload: SdfBenchWorkload.Rigs,
            Shape: SdfDebugShapeKind.Box,
            Op: SdfBenchOp.Baseline,
            InstanceCount: n
        );
    }
    /// <summary>A single shapes config for one <paramref name="shape"/> (fullscreen, no modifier) — the named
    /// workload a caller (e.g. a demo bench-scene adapter) swaps in directly at one explicit selection, without
    /// running the whole ladder.</summary>
    public static SdfBenchConfig Shape(SdfDebugShapeKind shape) =>
        new(
            Label: shape.ToString(),
            Workload: SdfBenchWorkload.Shapes,
            Shape: shape,
            Op: SdfBenchOp.Baseline,
            InstanceCount: 0
        );
    /// <summary>A single storm config for one <paramref name="mode"/> at <paramref name="count"/> instances — the
    /// label mirrors the ladder's own (<c>storm x{n}</c>, <c>storm rebuild x{n}</c>, <c>storm camera x{n}</c>). This
    /// is the rung a caller (e.g. a demo bench-scene adapter) requests directly (the 1024 rung).</summary>
    public static SdfBenchConfig StormRung(SdfBenchStormMode mode, int count) {
        var suffix = mode switch {
            SdfBenchStormMode.Rebuild => " rebuild",
            SdfBenchStormMode.Camera => " camera",
            _ => "",
        };

        return new SdfBenchConfig(
            Label: $"storm{suffix} x{count}",
            Workload: SdfBenchWorkload.Storm,
            Shape: SdfDebugShapeKind.Sphere,
            Op: SdfBenchOp.Baseline,
            InstanceCount: count,
            StormMode: mode
        );
    }
}
