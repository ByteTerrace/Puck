namespace Puck.Bench;

/// <summary>The per-scene verdict — which resource the delivered frame time was bound by (§6).</summary>
public enum BenchBound {
    /// <summary>Neither the GPU frame nor the CPU produce+pump reached the 85%-of-interval bar — pacing/mixed.</summary>
    Mixed = 0,
    /// <summary>The median GPU frame was at least 85% of the median interval.</summary>
    Gpu,
    /// <summary>The median CPU produce+pump was at least 85% of the median interval.</summary>
    Cpu,
}

/// <summary>Why a run produced no scored report — the loud-refusal reasons (§4 rule 4). <see cref="None"/> is a
/// clean scored run.</summary>
public enum BenchFailure {
    /// <summary>The run completed and produced scored scenes.</summary>
    None = 0,
    /// <summary>No <see cref="IBenchSceneController"/> scenes were registered — the suite is empty.</summary>
    NoScenes,
    /// <summary>No pass-timing source was attached — the harness cannot read GPU timestamps at all.</summary>
    NoTimingSource,
    /// <summary>A timing source is attached but no GPU timestamps are available — the harness refuses to report zeros.</summary>
    NoGpuTimestamps,
    /// <summary>The unknown suite name did not resolve to a registered scene set.</summary>
    UnknownSuite,
    /// <summary>The run was aborted (<c>bench.abort</c>) — nothing is reported.</summary>
    Aborted,
    /// <summary>A scene never reported ready within the ready watchdog window — the run is aborted rather than hung.</summary>
    SceneNeverReady,
    /// <summary>A sweep leg's switch override was rejected at apply time — the sweep is aborted rather than measuring a
    /// mislabeled leg against the wrong (unchanged) configuration.</summary>
    LegSwitchRejected,
}

/// <summary>
/// One named GPU render pass's binned timing within a scene result (e.g. <c>beam</c>, <c>views</c>). The label comes
/// from the <see cref="IBenchSceneController"/>'s producing engine via <c>IPassTimingSource.PassLabels</c>, so the
/// harness names no engine.
/// </summary>
/// <param name="Label">The render-pass label, in the source's declared order.</param>
/// <param name="Stats">The pass's per-frame GPU milliseconds, binned.</param>
public readonly record struct BenchPassResult(string Label, BenchChannelStats Stats);

/// <summary>
/// One scene's complete measured result — the neutral in-memory model a report sink (the console formatter, the JSON
/// writer) serializes. Every variance flag the run raised is carried here, so a report can never launder a dirty run
/// into clean-looking numbers.
/// </summary>
/// <param name="Name">The scene's dotted name.</param>
/// <param name="Category">The scene's family (<c>world</c>/<c>feature</c>).</param>
/// <param name="Weight">The scene's score weight (0 = reported, never scored).</param>
/// <param name="WarmFrames">The warm frames run before sampling.</param>
/// <param name="SampleFrames">The frames actually sampled.</param>
/// <param name="Wall">The wall-interval channel's throughput-and-variance statistics.</param>
/// <param name="GpuFrame">The whole-frame GPU milliseconds, binned (empty when no timestamps are available).</param>
/// <param name="Passes">The per-pass GPU breakdown, in source order.</param>
/// <param name="Pump">The launcher's pump-bucket CPU milliseconds, binned.</param>
/// <param name="GpuDrain">The launcher's gpu-drain-bucket CPU milliseconds, binned.</param>
/// <param name="Produce">The launcher's produce-bucket CPU milliseconds, binned.</param>
/// <param name="Present">The launcher's present-bucket CPU milliseconds, binned.</param>
/// <param name="Pacer">The launcher's pacer-bucket CPU milliseconds, binned.</param>
/// <param name="BeamMedianMs">The DVFS-canary reading — the beam pass's whole-scene median (0 when the source has no
/// beam pass).</param>
/// <param name="CanaryDrift">Whether this scored scene's beam-pass LAST-THIRD median drifted past the canary threshold
/// from its OWN FIRST-THIRD median — a within-scene, constant-workload clock-sag signal.</param>
/// <param name="Noisy">Whether the scene was noisy (p95/median &gt; 1.5).</param>
/// <param name="Paced">Whether the present cadence was capped for the run (in-session vsync) — an official score
/// comes only from the uncapped headless twin.</param>
/// <param name="Verdict">Which resource bound the delivered frame time.</param>
/// <param name="SceneFps">The scene's delivered throughput FPS.</param>
/// <param name="Score">The per-scene score (0 for an unscored/warmup scene or one with no reference constant).</param>
/// <param name="Samples">The raw per-frame wall intervals, present only when the run requested a raw dump; otherwise
/// <see langword="null"/>.</param>
public sealed record BenchSceneResult(
    string Name,
    string Category,
    double Weight,
    int WarmFrames,
    int SampleFrames,
    BenchWallStats Wall,
    BenchChannelStats GpuFrame,
    IReadOnlyList<BenchPassResult> Passes,
    BenchChannelStats Pump,
    BenchChannelStats GpuDrain,
    BenchChannelStats Produce,
    BenchChannelStats Present,
    BenchChannelStats Pacer,
    double BeamMedianMs,
    bool CanaryDrift,
    bool Noisy,
    bool Paced,
    BenchBound Verdict,
    double SceneFps,
    int Score,
    double[]? Samples
);

/// <summary>The overall score block for a run.</summary>
/// <param name="Overall">The geometric-mean composite (0 when the run produced no scored scene).</param>
/// <param name="ScoreFormula">The scoring-formula version string.</param>
/// <param name="Reference">The reference-configuration label.</param>
/// <param name="Capped">Whether any scored scene ran under a capped present cadence.</param>
/// <param name="Partial">Whether a scene the suite MEANT to score (weight &gt; 0) was dropped from the composite — it
/// had no reference constant or measured zero throughput — so the overall integrates fewer scenes than the roster
/// intends and is not directly comparable to a full run.</param>
public readonly record struct BenchOverallScore(int Overall, string ScoreFormula, string Reference, bool Capped, bool Partial);

/// <summary>
/// One complete benchmark run's outcome — the neutral in-memory model exposed on <see cref="BenchRuntime"/> and passed
/// to the completion seam a report sink (W2b) subscribes to. A clean scored run has <see cref="Failure"/>
/// <see cref="BenchFailure.None"/> and a populated <see cref="Scenes"/>/<see cref="Score"/>; a refused or aborted run
/// carries the reason and reports nothing.
/// </summary>
/// <param name="Suite">The suite name that ran.</param>
/// <param name="Failure">Why the run produced no scored report, or <see cref="BenchFailure.None"/>.</param>
/// <param name="StartedAtUtc">When the run started (UTC).</param>
/// <param name="DurationSeconds">The run's wall duration.</param>
/// <param name="IncludeSamples">Whether raw per-frame sample arrays were retained on each scene.</param>
/// <param name="SwitchName">For a sweep leg, the swept switch name; otherwise <see langword="null"/>.</param>
/// <param name="SwitchValue">For a sweep leg, the swept switch value; otherwise <see langword="null"/>.</param>
/// <param name="Scenes">The per-scene results, in run order (empty on a refused run).</param>
/// <param name="Score">The overall score block.</param>
public sealed record BenchRunOutcome(
    string Suite,
    BenchFailure Failure,
    DateTime StartedAtUtc,
    double DurationSeconds,
    bool IncludeSamples,
    string? SwitchName,
    string? SwitchValue,
    IReadOnlyList<BenchSceneResult> Scenes,
    BenchOverallScore Score
) {
    /// <summary>Whether the run produced a clean, scored result.</summary>
    public bool Succeeded => (Failure == BenchFailure.None);
}

/// <summary>
/// A whole-sweep outcome — the collected legs of a <c>bench.sweep</c>, one <see cref="BenchRunOutcome"/> per swept
/// value, for the combined summary a report sink prints after the last leg.
/// </summary>
/// <param name="SwitchName">The swept switch.</param>
/// <param name="Suite">The suite each leg ran.</param>
/// <param name="Legs">The per-value run outcomes, in sweep order.</param>
public sealed record BenchSweepOutcome(
    string SwitchName,
    string Suite,
    IReadOnlyList<BenchRunOutcome> Legs
);
