using System.Text.Json.Serialization;

namespace Puck.Bench;

/// <summary>
/// The <c>puck.bench.v1</c> JSON document (plan §8) — a sealed mirror of a completed
/// <see cref="BenchRunOutcome"/>/<see cref="BenchHostInfo"/> pair. Every property is a positional record parameter,
/// camelCase on the wire via <see cref="BenchReportJsonContext"/>, invariant-culture numerics (System.Text.Json numeric
/// writers are always invariant). The context is BOTH read and written: <see cref="BenchReportDocument.FromOutcome"/>
/// serializes a live outcome (the writer path), and <see cref="Puck.Bench.BenchReportComparer"/> deserializes two
/// on-disk reports back through the SAME metadata (the compare path). Because the read direction is real, a field
/// absent from an on-disk report deserializes to <see langword="null"/> (collections) or the type default — the
/// repo's own STJ null-collections gotcha applies, so a consumer of a DESERIALIZED report must normalize null
/// collections before use (the comparer does). The positional records still carry no field-backed members, so the
/// run-document initializer/<c>IncludeFields</c> gotchas do not apply on either path.
/// </summary>
/// <param name="Label">The render-pass label, in the timing source's declared order.</param>
/// <param name="Stats">The pass's binned per-frame GPU milliseconds.</param>
public readonly record struct BenchReportPass(string Label, BenchReportStats Stats);

/// <summary>The binned distribution for one channel of millisecond samples, exactly the five figures plan §8 names
/// (<c>{ meanMs, medianMs, p95Ms, p99Ms, minMs }</c>) — the report's per-channel unit.</summary>
/// <param name="MeanMs">The arithmetic mean, in milliseconds.</param>
/// <param name="MedianMs">The 50th-percentile value, in milliseconds.</param>
/// <param name="P95Ms">The 95th-percentile value, in milliseconds.</param>
/// <param name="P99Ms">The 99th-percentile value, in milliseconds.</param>
/// <param name="MinMs">The smallest value, in milliseconds.</param>
public readonly record struct BenchReportStats(double MeanMs, double MedianMs, double P95Ms, double P99Ms, double MinMs) {
    /// <summary>Projects a live <see cref="BenchChannelStats"/> down to the report's five-figure shape.</summary>
    /// <param name="stats">The runtime channel statistics.</param>
    /// <returns>The report's binned-stats block.</returns>
    public static BenchReportStats From(BenchChannelStats stats) =>
        new(MeanMs: stats.MeanMs, MedianMs: stats.MedianMs, MinMs: stats.MinMs, P95Ms: stats.P95Ms, P99Ms: stats.P99Ms);
}

/// <summary>The wall-interval channel's report block: the binned distribution plus the two throughput figures the
/// score model and stdout both print.</summary>
/// <param name="Stats">The binned wall-interval distribution (every sampled frame, spikes included).</param>
/// <param name="MeanFps">The scene's true delivered throughput — the non-spike frames' mean interval, inverted.</param>
/// <param name="OnePercentLowFps">The mean of the slowest one percent of frame times, inverted.</param>
public readonly record struct BenchReportWall(BenchReportStats Stats, double MeanFps, double OnePercentLowFps);

/// <summary>The GPU-side report block: the whole-frame distribution plus every named pass, keyed by label in the
/// timing source's declared order.</summary>
/// <param name="Frame">The whole-frame GPU milliseconds, binned (an empty/zeroed distribution when no timestamps
/// landed).</param>
/// <param name="Passes">Each render pass's binned milliseconds, keyed by its label.</param>
public readonly record struct BenchReportGpu(BenchReportStats Frame, IReadOnlyDictionary<string, BenchReportStats> Passes);

/// <summary>The launcher's CPU-bucket report block — the five buckets <see cref="Puck.Hosting.FrameTimingSample"/>
/// carries.</summary>
/// <param name="Pump">The pump-bucket milliseconds, binned (see <see cref="Puck.Hosting.FrameTimingSample.PumpMs"/>).</param>
/// <param name="GpuDrain">The gpu-drain-bucket milliseconds, binned.</param>
/// <param name="Produce">The produce-bucket milliseconds, binned.</param>
/// <param name="Present">The present-bucket milliseconds, binned.</param>
/// <param name="Pacer">The pacer-bucket milliseconds, binned.</param>
public readonly record struct BenchReportCpu(BenchReportStats Pump, BenchReportStats GpuDrain, BenchReportStats Produce, BenchReportStats Present, BenchReportStats Pacer);

/// <summary>The DVFS-canary report block (§6). <c>beamMedianMs</c> stays the beam pass's WHOLE-SCENE median; the
/// <c>canaryDrift</c> field carries the WITHIN-SCENE drift verdict (last-third vs first-third of the scene's own beam
/// samples), so a report reader sees the whole-scene beam figure and the honest clock-sag flag side by side.</summary>
/// <param name="BeamMedianMs">The beam pass's whole-scene median milliseconds (0 when the source has no beam pass).</param>
/// <param name="CanaryDrift">Whether this scored scene's beam-pass last-third median drifted past the canary threshold
/// from its own first-third median — a within-scene, constant-workload clock-sag signal.</param>
public readonly record struct BenchReportCanary(double BeamMedianMs, bool CanaryDrift);

/// <summary>The variance-flag report block (§6) — every flag a dirty run raises, so a report can never launder it
/// into clean-looking numbers.</summary>
/// <param name="Noisy">Whether the scene was noisy (p95/median &gt; 1.5).</param>
/// <param name="Paced">Whether the present cadence was assumed CAPPED for the run (in-session vsync) — an official
/// score comes only from the uncapped headless twin.</param>
public readonly record struct BenchReportFlags(bool Noisy, bool Paced);

/// <summary>One scene's complete report row.</summary>
/// <param name="Name">The scene's dotted name.</param>
/// <param name="Category">The scene's family (<c>world</c>/<c>feature</c>).</param>
/// <param name="Weight">The scene's score weight (0 = reported, never scored).</param>
/// <param name="WarmFrames">The warm frames run before sampling.</param>
/// <param name="SampleFrames">The frames actually sampled.</param>
/// <param name="SpikeFrames">The count of frames excluded from throughput scoring by the spike policy.</param>
/// <param name="Wall">The wall-interval throughput-and-variance block.</param>
/// <param name="Gpu">The GPU frame + per-pass block.</param>
/// <param name="Cpu">The CPU-bucket block.</param>
/// <param name="Canary">The DVFS-canary block.</param>
/// <param name="Flags">The variance flags.</param>
/// <param name="Verdict">Which resource bound the delivered frame time — <c>"GPU"</c>, <c>"CPU"</c>, or
/// <c>"MIXED"</c>.</param>
/// <param name="Score">The per-scene score (0 for an unscored/warmup scene or one with no reference constant).</param>
/// <param name="Samples">The raw per-frame wall intervals, present only when the run requested a raw dump.</param>
public sealed record BenchReportScene(
    string Name,
    string Category,
    double Weight,
    int WarmFrames,
    int SampleFrames,
    int SpikeFrames,
    BenchReportWall Wall,
    BenchReportGpu Gpu,
    BenchReportCpu Cpu,
    BenchReportCanary Canary,
    BenchReportFlags Flags,
    string Verdict,
    int Score,
    double[]? Samples
);

/// <summary>The build the report was produced by.</summary>
/// <param name="GitCommit">The short commit hash, or <see cref="BenchHostInfo.Unknown"/>.</param>
/// <param name="GitBranch">The branch name, or <see cref="BenchHostInfo.Unknown"/>.</param>
/// <param name="Configuration">The build configuration (<c>Debug</c>/<c>Release</c>).</param>
public readonly record struct BenchReportEngine(string GitCommit, string GitBranch, string Configuration);

/// <summary>The machine/session the report ran on.</summary>
/// <param name="Os">The OS description.</param>
/// <param name="ProcessorCount">The logical processor count.</param>
/// <param name="GpuName">The active GPU's reported name, or <see cref="BenchHostInfo.Unknown"/>.</param>
/// <param name="Backend">The active render backend, or <see cref="BenchHostInfo.Unknown"/>.</param>
/// <param name="Resolution">The swapchain resolution as <c>[width, height]</c>.</param>
/// <param name="PresentMode">The swapchain present mode, or <see cref="BenchHostInfo.Unknown"/>.</param>
/// <param name="PresentRateTier">The live <c>present.rate</c> tier at attach time.</param>
/// <param name="RenderScaleTier">The live <c>render.scale</c> tier at attach time.</param>
public readonly record struct BenchReportHost(
    string Os,
    int ProcessorCount,
    string GpuName,
    string Backend,
    int[] Resolution,
    string PresentMode,
    string PresentRateTier,
    string RenderScaleTier
);

/// <summary>The overall score block.</summary>
/// <param name="Overall">The geometric-mean composite (0 when the run produced no scored scene).</param>
/// <param name="Reference">The reference-configuration label.</param>
/// <param name="Capped">Whether any scored scene ran under a capped present cadence.</param>
/// <param name="Partial">Whether a scene the roster meant to score was dropped from the composite (no reference
/// constant or zero measured throughput), so the overall integrates fewer scenes than a full run.</param>
public readonly record struct BenchReportScore(int Overall, string Reference, bool Capped, bool Partial);

/// <summary>
/// The complete <c>puck.bench.v1</c> report document. <see cref="FromOutcome"/> is the ONLY production conversion
/// from a live <see cref="BenchRunOutcome"/> — a refused/aborted outcome (<see cref="BenchRunOutcome.Succeeded"/>
/// <see langword="false"/>) has nothing to report and must not reach this constructor; <see cref="Puck.Bench.BenchReportWriter"/>
/// enforces that gate before ever calling in.
/// </summary>
/// <param name="Kind">The document kind discriminator — always <c>"puck.bench.v1"</c>.</param>
/// <param name="ScoreFormula">The scoring-formula version string (see <see cref="BenchScoreModel.ScoreFormula"/>).</param>
/// <param name="StartedAtUtc">When the run started, truncated to whole seconds (UTC).</param>
/// <param name="DurationSeconds">The run's wall duration.</param>
/// <param name="Engine">The build the report was produced by.</param>
/// <param name="Host">The machine/session the report ran on.</param>
/// <param name="FeatureSwitches">Every registered switch's value at the moment this leg completed.</param>
/// <param name="Suite">The suite name that ran.</param>
/// <param name="SweepSwitch">For a sweep leg, the swept switch name; omitted (null) for a plain run.</param>
/// <param name="SweepValue">For a sweep leg, this leg's swept value; omitted (null) for a plain run.</param>
/// <param name="Scenes">The per-scene report rows, in run order.</param>
/// <param name="Score">The overall score block.</param>
public sealed record BenchReportDocument(
    string Kind,
    string ScoreFormula,
    DateTime StartedAtUtc,
    double DurationSeconds,
    BenchReportEngine Engine,
    BenchReportHost Host,
    IReadOnlyDictionary<string, string> FeatureSwitches,
    string Suite,
    string? SweepSwitch,
    string? SweepValue,
    IReadOnlyList<BenchReportScene> Scenes,
    BenchReportScore Score
) {
    /// <summary>The document-kind discriminator every report stamps.</summary>
    public const string DocumentKind = "puck.bench.v1";

    /// <summary>Builds the report document from a clean, scored run outcome. The caller (<see cref="Puck.Bench.BenchReportWriter"/>)
    /// is responsible for never calling this on a refused/aborted outcome.</summary>
    /// <param name="outcome">The completed, scored run/leg outcome.</param>
    /// <param name="host">The host facts to stamp (git/build/GPU/backend/tier/resolution).</param>
    /// <param name="featureSwitches">Every registered switch's value at completion time.</param>
    /// <returns>The report document, ready to serialize.</returns>
    public static BenchReportDocument FromOutcome(BenchRunOutcome outcome, BenchHostInfo host, IReadOnlyDictionary<string, string> featureSwitches) {
        ArgumentNullException.ThrowIfNull(argument: outcome);
        ArgumentNullException.ThrowIfNull(argument: host);
        ArgumentNullException.ThrowIfNull(argument: featureSwitches);

        var scenes = new BenchReportScene[outcome.Scenes.Count];

        for (var index = 0; (index < outcome.Scenes.Count); index++) {
            scenes[index] = ToReportScene(scene: outcome.Scenes[index]);
        }

        var startedAtUtc = outcome.StartedAtUtc;

        return new BenchReportDocument(
            DurationSeconds: outcome.DurationSeconds,
            Engine: new BenchReportEngine(Configuration: host.Configuration, GitBranch: host.GitBranch, GitCommit: host.GitCommit),
            FeatureSwitches: featureSwitches,
            Host: new BenchReportHost(
                Backend: host.Backend,
                GpuName: host.GpuName,
                Os: host.Os,
                PresentMode: host.PresentMode,
                PresentRateTier: host.PresentRateTier,
                ProcessorCount: host.ProcessorCount,
                RenderScaleTier: host.RenderScaleTier,
                Resolution: [host.ResolutionWidth, host.ResolutionHeight]
            ),
            Kind: DocumentKind,
            Scenes: scenes,
            Score: new BenchReportScore(Capped: outcome.Score.Capped, Overall: outcome.Score.Overall, Partial: outcome.Score.Partial, Reference: outcome.Score.Reference),
            ScoreFormula: outcome.Score.ScoreFormula,
            StartedAtUtc: new DateTime(ticks: (startedAtUtc.Ticks - (startedAtUtc.Ticks % TimeSpan.TicksPerSecond)), kind: DateTimeKind.Utc),
            Suite: outcome.Suite,
            SweepSwitch: outcome.SwitchName,
            SweepValue: outcome.SwitchValue
        );
    }

    private static BenchReportScene ToReportScene(BenchSceneResult scene) {
        var passes = new Dictionary<string, BenchReportStats>(capacity: scene.Passes.Count, comparer: StringComparer.Ordinal);

        foreach (var pass in scene.Passes) {
            passes[pass.Label] = BenchReportStats.From(stats: pass.Stats);
        }

        return new BenchReportScene(
            Canary: new BenchReportCanary(BeamMedianMs: scene.BeamMedianMs, CanaryDrift: scene.CanaryDrift),
            Category: scene.Category,
            Cpu: new BenchReportCpu(
                GpuDrain: BenchReportStats.From(stats: scene.GpuDrain),
                Pacer: BenchReportStats.From(stats: scene.Pacer),
                Present: BenchReportStats.From(stats: scene.Present),
                Produce: BenchReportStats.From(stats: scene.Produce),
                Pump: BenchReportStats.From(stats: scene.Pump)
            ),
            Flags: new BenchReportFlags(Noisy: scene.Noisy, Paced: scene.Paced),
            Gpu: new BenchReportGpu(Frame: BenchReportStats.From(stats: scene.GpuFrame), Passes: passes),
            Name: scene.Name,
            Samples: scene.Samples,
            SampleFrames: scene.SampleFrames,
            Score: scene.Score,
            SpikeFrames: scene.Wall.SpikeFrames,
            Verdict: BenchVerdictText.Format(verdict: scene.Verdict),
            Wall: new BenchReportWall(MeanFps: scene.Wall.ThroughputFps, OnePercentLowFps: scene.Wall.OnePercentLowFps, Stats: BenchReportStats.From(stats: scene.Wall.Binned)),
            WarmFrames: scene.WarmFrames,
            Weight: scene.Weight
        );
    }
}

/// <summary>The shared <see cref="BenchBound"/> ↔ report/stdout text mapping — <c>"GPU"</c>/<c>"CPU"</c>/<c>"MIXED"</c>,
/// used identically by the JSON <c>verdict</c> field and the stdout <c>bound=</c> token so the two never disagree.</summary>
public static class BenchVerdictText {
    /// <summary>Formats a verdict as its report/stdout token.</summary>
    /// <param name="verdict">The computed verdict.</param>
    /// <returns><c>"GPU"</c>, <c>"CPU"</c>, or <c>"MIXED"</c>.</returns>
    public static string Format(BenchBound verdict) =>
        verdict switch {
            BenchBound.Gpu => "GPU",
            BenchBound.Cpu => "CPU",
            _ => "MIXED",
        };
}

/// <summary>
/// The System.Text.Json SOURCE-GENERATION context for the <c>puck.bench.v1</c> report — the only sanctioned
/// serialization entry point (the reflection-JSON canary). CamelCase matches plan §8's wire shape exactly;
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/> is what lets a plain (non-sweep) run's <c>sweepSwitch</c>/
/// <c>sweepValue</c> and a samples-free scene's <c>samples</c> disappear from the document entirely rather than
/// serializing as an explicit <c>null</c> — the plan's exact §8 shape is the samples-free, non-sweep case.
/// <para>
/// The default (metadata + serialization) generation mode makes this context serve BOTH directions: the report
/// WRITER (<see cref="Puck.Bench.BenchReportWriter"/>) serializes through it, and the cross-run COMPARER
/// (<see cref="Puck.Bench.BenchReportComparer"/>) deserializes two on-disk reports back through the same metadata —
/// so the read and write shapes can never drift. The records stay init-only positional shapes with no field-backed
/// members, so the run-document <c>IncludeFields</c>/initializer gotchas do not apply on either path.
/// </para>
/// </summary>
[JsonSerializable(typeof(BenchReportDocument))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
internal sealed partial class BenchReportJsonContext : JsonSerializerContext {
}
