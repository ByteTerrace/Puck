using System.Diagnostics;

using Puck.Abstractions.Gpu;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.Bench;

/// <summary>
/// The per-frame benchmark state machine (§5). It advances on <see cref="FrameTimingHub.Published"/> — the render
/// thread — reading <see cref="IPassTimingSource.TryReadPassTimings"/> alongside each hub sample. The path is
/// <c>Idle → Arm → [per leg: LegBegin → [per scene: Setup → AwaitReady → WarmSample → Teardown] → LegScoreReport] →
/// Restore → Idle</c>; a plain <c>bench.run</c> is a one-leg plan, a <c>bench.sweep</c> is an N-leg plan whose snapshot
/// and restore bracket the WHOLE sweep. The per-frame handler is allocation-light: it only appends doubles to
/// pre-sized <see cref="BenchSampleSet"/>s and reads a pre-allocated timing scratch span; every sort/allocation happens
/// once per scene at finalize, off the hot path. Commands (<c>bench.run</c>/<c>bench.abort</c>) latch a request on the
/// caller thread; the render thread is the ONLY mutator of the machine state.
/// </summary>
internal sealed class BenchRunner {
    // The scene-readiness watchdog (§5): if a scene's IsReady never returns true, the state machine would await forever
    // (a headless --bench then hangs with no window to close). Past this many accumulated frame-interval milliseconds
    // the run aborts loudly with SceneNeverReady rather than wedging.
    private const double ReadyTimeoutMs = 20_000.0;

    private enum RunnerState {
        Idle = 0,
        Arm,
        LegBegin,
        SceneSetup,
        SceneAwaitReady,
        SceneWarmSample,
        SceneTeardown,
        LegScoreReport,
        Restore,
    }

    // One leg of a plan: an optional switch override applied before the leg's scenes run (null for a plain run).
    internal sealed record RunLeg(string? SwitchName, string? SwitchValue);

    // A complete run request, captured on the caller thread and handed to the render thread through m_pendingPlan.
    internal sealed record RunPlan(
        string Suite,
        IReadOnlyList<BenchSceneDescriptor> Scenes,
        bool IncludeSamples,
        string? SweepSwitch,
        IReadOnlyList<RunLeg> Legs,
        bool PriorArmed,
        DateTime StartedAtUtc,
        long StartTimestamp
    );

    private readonly BenchRuntime m_owner;

    // Cross-thread latches (caller thread writes, render thread reads/clears).
    private volatile RunPlan? m_pendingPlan;
    private int m_active;
    private int m_abortRequested;

    // Render-thread-only state.
    private RunnerState m_state;
    private RunPlan? m_plan;
    private BenchSceneDescriptor? m_scene;
    private int m_legIndex;
    private int m_sceneIndex;
    private int m_sceneFrame;
    private int m_beamPassIndex = -1;
    private int m_gpuReadsLanded;
    private double m_awaitReadyMs;
    private FeatureSwitchSnapshot? m_snapshot;
    private double[] m_passScratch = [];
    private readonly List<BenchSceneResult> m_legScenes = [];
    private readonly List<BenchRunOutcome> m_sweepLegs = [];

    // Per-scene accumulators, reallocated at each scene's warm-start (sized to the scene's sample count).
    private BenchSampleSet m_wall = new(capacity: 1);
    private BenchSampleSet m_gpuFrame = new(capacity: 1);
    private BenchSampleSet m_pump = new(capacity: 1);
    private BenchSampleSet m_gpuDrain = new(capacity: 1);
    private BenchSampleSet m_produce = new(capacity: 1);
    private BenchSampleSet m_present = new(capacity: 1);
    private BenchSampleSet m_pacer = new(capacity: 1);
    private BenchSampleSet[] m_passes = [];

    /// <summary>Creates a runner bound to its owning <see cref="BenchRuntime"/> (the seam accessors and event sink).</summary>
    /// <param name="owner">The runtime that attaches the timing/switch/console seams and raises the result events.</param>
    public BenchRunner(BenchRuntime owner) {
        m_owner = owner;
    }

    /// <summary>Whether a run is currently latched or executing (a second run is rejected while true).</summary>
    public bool IsRunning => (Volatile.Read(location: ref m_active) != 0);

    /// <summary>Latches a run request (caller thread). Claims the runner (rejecting a concurrent run), arms GPU timing so
    /// the frame-timing hub begins publishing and the state machine can advance, and hands the plan to the render
    /// thread.</summary>
    /// <param name="plan">The captured run plan.</param>
    /// <returns><see langword="true"/> when the request was accepted; <see langword="false"/> when a run is already in
    /// progress.</returns>
    internal bool RequestRun(RunPlan plan) {
        if (Interlocked.CompareExchange(location1: ref m_active, value: 1, comparand: 0) != 0) {
            return false;
        }

        Volatile.Write(location: ref m_abortRequested, value: 0);

        // Arm timing so the launcher publishes frame samples; the plan carries the prior state for restore.
        GpuTimingControl.Shared.SetArmed(armed: true);
        m_pendingPlan = plan;

        return true;
    }

    /// <summary>Requests that the active run abort (caller thread) — the render thread finishes the current frame, runs
    /// the active scene's teardown script, restores the snapshot, and reports nothing scored.</summary>
    /// <returns>A status line for the console.</returns>
    public string RequestAbort() {
        if (Volatile.Read(location: ref m_active) == 0) {
            return "[bench: nothing is running]";
        }

        Volatile.Write(location: ref m_abortRequested, value: 1);

        // The DISARMED-ESCAPE HATCH. The state machine advances ONLY on FrameTimingHub.Published, and the launcher
        // publishes only while GPU timing is armed. If timing was disarmed mid-run (a feature.reset, or any path that
        // slipped a gpu.timing=off past the setter guard), no further frame will ever publish — so the render thread
        // would never observe this latched abort and the run would wedge forever. When timing is disarmed AND a run is
        // active, the render thread is provably not stepping the machine (publishes are off), so this abort is finished
        // INLINE here on the caller thread; there is no publish to race it.
        if (!GpuTimingControl.Shared.Armed && (Volatile.Read(location: ref m_active) != 0)) {
            EscapeAbortWhileDisarmed();

            return "[bench: abort restored inline — GPU timing was disarmed, so no published frame could carry it]";
        }

        return "[bench: abort requested — finishing the current frame, then restoring]";
    }

    // Finishes a latched abort inline when GPU timing is disarmed (see RequestAbort). Publishes are off, so the render
    // thread is not touching runner state: adopt any still-pending plan (the abort raced the run's very start) so the
    // outcome carries it, then run the standard aborted-terminal path (teardown + restore + RunCompleted).
    private void EscapeAbortWhileDisarmed() {
        if ((m_state == RunnerState.Idle) && (m_pendingPlan is { } pending)) {
            m_pendingPlan = null;
            m_plan = pending;
        }

        HandleAbort();
    }

    /// <summary>Builds a one-leg (plain) run plan capturing the current arming state and clock.</summary>
    /// <param name="suite">The suite name.</param>
    /// <param name="scenes">The suite's scenes, in order.</param>
    /// <param name="includeSamples">Whether to retain raw per-frame wall samples on each scene.</param>
    /// <returns>The captured plan.</returns>
    internal static RunPlan PlanRun(string suite, IReadOnlyList<BenchSceneDescriptor> scenes, bool includeSamples) =>
        new(
            IncludeSamples: includeSamples,
            Legs: [new RunLeg(SwitchName: null, SwitchValue: null)],
            PriorArmed: GpuTimingControl.Shared.Armed,
            Scenes: scenes,
            StartedAtUtc: DateTime.UtcNow,
            StartTimestamp: Stopwatch.GetTimestamp(),
            Suite: suite,
            SweepSwitch: null
        );

    /// <summary>Builds an N-leg sweep plan — the suite run once per value, one leg per value.</summary>
    /// <param name="suite">The suite name.</param>
    /// <param name="scenes">The suite's scenes, in order.</param>
    /// <param name="switchName">The switch swept across the legs.</param>
    /// <param name="values">The values to sweep, in order.</param>
    /// <returns>The captured plan.</returns>
    internal static RunPlan PlanSweep(string suite, IReadOnlyList<BenchSceneDescriptor> scenes, string switchName, IReadOnlyList<string> values) {
        var legs = new RunLeg[values.Count];

        for (var index = 0; (index < values.Count); index++) {
            legs[index] = new RunLeg(SwitchName: switchName, SwitchValue: values[index]);
        }

        return new RunPlan(
            IncludeSamples: false,
            Legs: legs,
            PriorArmed: GpuTimingControl.Shared.Armed,
            Scenes: scenes,
            StartedAtUtc: DateTime.UtcNow,
            StartTimestamp: Stopwatch.GetTimestamp(),
            Suite: suite,
            SweepSwitch: switchName
        );
    }

    /// <summary>The frame-timing hub subscriber — one state-machine step per published frame. Tiny by contract: it steps
    /// an enum and appends a few doubles; the heavy work is deferred to per-scene finalize.</summary>
    /// <param name="sample">The frame's CPU-side timing buckets.</param>
    public void OnFramePublished(FrameTimingSample sample) {
        if (Volatile.Read(location: ref m_abortRequested) != 0) {
            if (m_state != RunnerState.Idle) {
                HandleAbort();
            } else if (m_pendingPlan is { } pending) {
                // The abort RACED the run's start: the plan is latched (m_pendingPlan) but this thread hasn't stepped
                // out of Idle yet, so there is no active scene to tear down. Adopt the pending plan and abort it cleanly
                // — otherwise the run would deadlock here forever (Idle skips HandleAbort, and this early return skips
                // StepIdle), never restoring, never raising RunCompleted, never releasing m_active. Nothing was armed
                // yet (no snapshot, present.rate untouched), so this is a pure "never started" abort.
                m_pendingPlan = null;
                m_plan = pending;
                HandleAbort();
            } else {
                // A stray abort with nothing latched and nothing running — clear the request so it can't wedge a later
                // run (RequestAbort only latches when m_active, so this is belt-and-braces).
                Volatile.Write(location: ref m_abortRequested, value: 0);
            }

            return;
        }

        switch (m_state) {
            case RunnerState.Idle: StepIdle(); break;
            case RunnerState.Arm: StepArm(); break;
            case RunnerState.LegBegin: StepLegBegin(); break;
            case RunnerState.SceneSetup: StepSceneSetup(); break;
            case RunnerState.SceneAwaitReady: StepAwaitReady(sample: sample); break;
            case RunnerState.SceneWarmSample: StepWarmSample(sample: sample); break;
            case RunnerState.SceneTeardown: StepTeardown(); break;
            case RunnerState.LegScoreReport: StepLegScoreReport(); break;
            case RunnerState.Restore: StepRestore(); break;
            default: break;
        }
    }

    // Idle: pick up a latched plan and begin.
    private void StepIdle() {
        if (m_pendingPlan is not { } plan) {
            return;
        }

        m_pendingPlan = null;
        m_plan = plan;
        m_legIndex = 0;
        m_gpuReadsLanded = 0;
        m_sweepLegs.Clear();
        m_state = RunnerState.Arm;
    }

    // Arm (once per plan): snapshot the switches, force the uncapped present cadence, resolve the beam pass, size the
    // timing scratch. Timing itself was armed by RequestRun so the hub could reach us here.
    private void StepArm() {
        if (m_owner.Switches is { } switches) {
            m_snapshot = switches.Snapshot();
            ForcePresentDisplay(switches: switches);
        } else {
            m_snapshot = null;
        }

        m_beamPassIndex = FindBeamPass();
        m_passScratch = new double[Math.Max(val1: 1, val2: (m_owner.TimingSource?.PassCount ?? 0))];
        m_state = RunnerState.LegBegin;
    }

    // LegBegin: apply this leg's switch override (sweep), reset the leg's scene accumulation.
    private void StepLegBegin() {
        if (m_plan is not { } plan) {
            return;
        }

        var leg = plan.Legs[m_legIndex];

        if (!ApplyLegSwitch(leg: leg)) {
            // The leg's switch override was REFUSED (a boot-only switch, a value the lever rejected). Proceeding would
            // measure and label this leg with a value that never took effect — abort loudly instead of publishing a
            // mislabeled report (§4 honesty rule).
            Console.Out.WriteLine(value: $"[bench] sweep leg '{leg.SwitchName}={leg.SwitchValue}' was rejected by the switch — aborting the sweep rather than measuring a mislabeled leg.");
            FinishWithFailure(failure: BenchFailure.LegSwitchRejected);

            return;
        }

        m_legScenes.Clear();
        m_sceneIndex = 0;
        BeginScene();
    }

    // SceneSetup: submit the scene's setup script, then wait on readiness.
    private void StepSceneSetup() {
        if (m_scene is { } scene) {
            SubmitScript(lines: scene.Controller.SetupScript);
        }

        m_state = RunnerState.SceneAwaitReady;
    }

    // AwaitReady: poll the controller each frame; allocate accumulators and warm once ready. Guarded by a watchdog —
    // a scene whose readiness never lands (a boot that silently failed, a stuck layout) would otherwise await forever,
    // hanging a headless --bench with no window to close.
    private void StepAwaitReady(FrameTimingSample sample) {
        if (m_scene is not { } scene) {
            return;
        }

        if (scene.Controller.IsReady()) {
            AllocateSceneAccumulators(sampleFrames: scene.SampleFrames);
            m_sceneFrame = 0;
            m_state = RunnerState.SceneWarmSample;

            return;
        }

        m_awaitReadyMs += sample.IntervalMs;

        if (m_awaitReadyMs > ReadyTimeoutMs) {
            Console.Out.WriteLine(value: $"[bench] scene '{scene.Name}' never became ready after {ReadyTimeoutMs:N0} ms — aborting the run.");
            SubmitScript(lines: scene.Controller.TeardownScript);
            FinishWithFailure(failure: BenchFailure.SceneNeverReady);
        }
    }

    // WarmSample: drive the controller every frame; record once past the warm window; finalize at the end.
    private void StepWarmSample(FrameTimingSample sample) {
        if (m_scene is not { } scene) {
            return;
        }

        scene.Controller.OnFrame(frameIndex: m_sceneFrame);

        if (m_sceneFrame >= scene.WarmFrames) {
            RecordSample(sample: sample);
        }

        m_sceneFrame++;

        if (m_sceneFrame < (scene.WarmFrames + scene.SampleFrames)) {
            return;
        }

        // Loud no-timestamp refusal (§4 rule 4): if the very first scene sampled with a source attached and NOTHING
        // landed, the harness cannot read GPU timestamps — abort loudly rather than report zeros.
        if ((m_legIndex == 0) && (m_sceneIndex == 0) && (m_gpuReadsLanded == 0)) {
            SubmitScript(lines: scene.Controller.TeardownScript);
            FinishWithFailure(failure: BenchFailure.NoGpuTimestamps);

            return;
        }

        FinalizeScene(scene: scene);
        m_state = RunnerState.SceneTeardown;
    }

    // Teardown: submit the scene's teardown script, advance to the next scene or the leg's scoring.
    private void StepTeardown() {
        if (m_scene is { } scene) {
            SubmitScript(lines: scene.Controller.TeardownScript);
        }

        m_sceneIndex++;

        if ((m_plan is { } plan) && (m_sceneIndex < plan.Scenes.Count)) {
            BeginScene();
        } else {
            m_state = RunnerState.LegScoreReport;
        }
    }

    // LegScoreReport: build and raise the leg's outcome; advance to the next leg or restore.
    private void StepLegScoreReport() {
        if (m_plan is not { } plan) {
            return;
        }

        var outcome = BuildOutcome(failure: BenchFailure.None);

        m_sweepLegs.Add(item: outcome);
        m_owner.RaiseRunCompleted(outcome: outcome);
        m_legIndex++;
        m_state = ((m_legIndex < plan.Legs.Count) ? RunnerState.LegBegin : RunnerState.Restore);
    }

    // Restore: put the environment back verbatim; raise the sweep summary; go idle.
    private void StepRestore() {
        RestoreEnvironment();

        if ((m_plan is { SweepSwitch: { } sweepSwitch } plan)) {
            m_owner.RaiseSweepCompleted(sweep: new BenchSweepOutcome(
                Legs: m_sweepLegs.ToArray(),
                Suite: plan.Suite,
                SwitchName: sweepSwitch
            ));
        }

        m_scene = null;
        m_state = RunnerState.Idle;
        Volatile.Write(location: ref m_active, value: 0);
    }

    // Abort: finish this frame, run the active scene's teardown, restore, report an aborted outcome (nothing scored).
    private void HandleAbort() {
        if (m_scene is { } scene) {
            SubmitScript(lines: scene.Controller.TeardownScript);
        }

        FinishWithFailure(failure: BenchFailure.Aborted);
    }

    // Common terminal path for a refused/aborted run: restore, go idle, raise an empty outcome carrying the reason.
    private void FinishWithFailure(BenchFailure failure) {
        var plan = m_plan;

        RestoreEnvironment();

        m_scene = null;
        m_state = RunnerState.Idle;
        Volatile.Write(location: ref m_abortRequested, value: 0);
        Volatile.Write(location: ref m_active, value: 0);

        if (plan is null) {
            return;
        }

        m_owner.RaiseRunCompleted(outcome: new BenchRunOutcome(
            DurationSeconds: Stopwatch.GetElapsedTime(startingTimestamp: plan.StartTimestamp).TotalSeconds,
            Failure: failure,
            IncludeSamples: plan.IncludeSamples,
            Scenes: [],
            Score: new BenchOverallScore(Capped: false, Overall: 0, Partial: false, Reference: BenchScoreModel.ReferenceLabel, ScoreFormula: BenchScoreModel.ScoreFormula),
            StartedAtUtc: plan.StartedAtUtc,
            Suite: plan.Suite,
            SwitchName: null,
            SwitchValue: null
        ));
    }

    // Enters the scene at m_sceneIndex and sets up its setup-script submission.
    private void BeginScene() {
        if (m_plan is not { } plan) {
            return;
        }

        m_scene = plan.Scenes[m_sceneIndex];
        m_awaitReadyMs = 0.0;
        m_state = RunnerState.SceneSetup;
    }

    // Sizes the per-scene accumulators to the scene's sample count (allocation happens here, once per scene, never on
    // the per-frame path).
    private void AllocateSceneAccumulators(int sampleFrames) {
        var capacity = Math.Max(val1: 1, val2: sampleFrames);

        m_wall = new BenchSampleSet(capacity: capacity);
        m_gpuFrame = new BenchSampleSet(capacity: capacity);
        m_pump = new BenchSampleSet(capacity: capacity);
        m_gpuDrain = new BenchSampleSet(capacity: capacity);
        m_produce = new BenchSampleSet(capacity: capacity);
        m_present = new BenchSampleSet(capacity: capacity);
        m_pacer = new BenchSampleSet(capacity: capacity);

        var passCount = m_passScratch.Length;

        m_passes = new BenchSampleSet[passCount];

        for (var index = 0; (index < passCount); index++) {
            m_passes[index] = new BenchSampleSet(capacity: capacity);
        }
    }

    // Records one sampled frame: the wall interval, the CPU buckets, and (when a read lands) the GPU frame + per-pass.
    private void RecordSample(FrameTimingSample sample) {
        m_wall.Add(milliseconds: sample.IntervalMs);
        m_pump.Add(milliseconds: sample.PumpMs);
        m_gpuDrain.Add(milliseconds: sample.GpuDrainMs);
        m_produce.Add(milliseconds: sample.ProduceMs);
        m_present.Add(milliseconds: sample.PresentMs);
        m_pacer.Add(milliseconds: sample.PacerMs);

        if (m_owner.TimingSource is not { } source) {
            return;
        }

        if (!source.TryReadPassTimings(passMilliseconds: m_passScratch, passCount: out var passCount, frameMilliseconds: out var frameMs)) {
            return;
        }

        m_gpuFrame.Add(milliseconds: frameMs);
        m_gpuReadsLanded++;

        var count = Math.Min(val1: passCount, val2: m_passes.Length);

        for (var index = 0; (index < count); index++) {
            m_passes[index].Add(milliseconds: m_passScratch[index]);
        }
    }

    // Finalizes a scene: binds every channel's stats, computes the verdict/score/canary, raises SceneCompleted.
    private void FinalizeScene(BenchSceneDescriptor scene) {
        var wall = m_wall.WallStats(spikeFactor: BenchScoreModel.SpikeFactor);
        var gpuFrame = m_gpuFrame.Stats();
        var passes = BuildPassResults();
        var sceneFps = wall.ThroughputFps;
        var beamMedian = (((m_beamPassIndex >= 0) && (m_beamPassIndex < m_passes.Length)) ? m_passes[m_beamPassIndex].Median() : 0.0);
        var scored = (scene.Weight > 0.0);
        var canaryDrift = ResolveCanaryDrift(scored: scored);
        var verdict = ComputeVerdict(medianInterval: wall.Binned.MedianMs, medianGpu: gpuFrame.MedianMs, medianProduce: m_produce.Median(), medianPump: m_pump.Median());
        var score = (BenchScoreModel.TryGetReferenceFps(sceneName: scene.Name, referenceFps: out var referenceFps) ? BenchScoreModel.SceneScore(sceneFps: sceneFps, referenceFps: referenceFps) : 0);
        var samples = ((m_plan?.IncludeSamples ?? false) ? m_wall.ToArray() : null);

        var result = new BenchSceneResult(
            BeamMedianMs: beamMedian,
            CanaryDrift: canaryDrift,
            Category: scene.Category,
            GpuDrain: m_gpuDrain.Stats(),
            GpuFrame: gpuFrame,
            Name: scene.Name,
            Noisy: wall.Noisy,
            Pacer: m_pacer.Stats(),
            Paced: m_owner.AssumePacedPresent,
            Passes: passes,
            Present: m_present.Stats(),
            Produce: m_produce.Stats(),
            Pump: m_pump.Stats(),
            SampleFrames: m_wall.Count,
            Samples: samples,
            Score: score,
            SceneFps: sceneFps,
            Verdict: verdict,
            Wall: wall,
            WarmFrames: scene.WarmFrames,
            Weight: scene.Weight
        );

        m_legScenes.Add(item: result);
        m_owner.RaiseSceneCompleted(result: result);
    }

    // The WITHIN-SCENE DVFS canary (§6): a scene marches ONE constant workload, so its own late frames should time
    // like its early frames — a drift between them is a clean clock-sag signal that no cross-scene workload difference
    // can forge. Splits the beam pass's own samples (in time order) into a first-third and a last-third and flags the
    // scene when the last-third median drifts past the threshold from the first-third. Unscored scenes (the warmup rung,
    // where clocks legitimately spin UP toward their plateau) are exempt — their drift is expected, not a fault.
    private bool ResolveCanaryDrift(bool scored) {
        if (!scored || (m_beamPassIndex < 0) || (m_beamPassIndex >= m_passes.Length)) {
            return false;
        }

        var beam = m_passes[m_beamPassIndex];
        var firstThird = beam.MedianOfTimeSlice(startFraction: 0.0, endFraction: (1.0 / 3.0));
        var lastThird = beam.MedianOfTimeSlice(startFraction: (2.0 / 3.0), endFraction: 1.0);

        if (firstThird <= 0.0) {
            return false;
        }

        return ((Math.Abs(value: (lastThird - firstThird)) / firstThird) > BenchScoreModel.CanaryDriftFraction);
    }

    // Builds the per-pass result rows from the source labels and each pass's accumulator.
    private BenchPassResult[] BuildPassResults() {
        var labels = ((m_owner.TimingSource is { } source) ? source.PassLabels : default);
        var results = new BenchPassResult[m_passes.Length];

        for (var index = 0; (index < m_passes.Length); index++) {
            var label = ((index < labels.Length) ? labels[index] : $"pass{index}");

            results[index] = new BenchPassResult(Label: label, Stats: m_passes[index].Stats());
        }

        return results;
    }

    // The §6 verdict: GPU-bound when the median GPU frame is ≥85% of the median interval; CPU-bound when produce+pump
    // is ≥85%; otherwise pacing/mixed.
    private static BenchBound ComputeVerdict(double medianInterval, double medianGpu, double medianProduce, double medianPump) {
        if (medianInterval <= 0.0) {
            return BenchBound.Mixed;
        }

        if (medianGpu >= (0.85 * medianInterval)) {
            return BenchBound.Gpu;
        }

        if ((medianProduce + medianPump) >= (0.85 * medianInterval)) {
            return BenchBound.Cpu;
        }

        return BenchBound.Mixed;
    }

    // Assembles the leg's outcome: the scored terms → overall, the capped flag, the elapsed duration.
    private BenchRunOutcome BuildOutcome(BenchFailure failure) {
        var plan = m_plan!;
        var leg = plan.Legs[m_legIndex];
        var terms = new List<(double sceneFps, double referenceFps, double weight)>();
        var capped = false;
        var partial = false;

        foreach (var scene in m_legScenes) {
            if (scene.Weight <= 0.0) {
                continue;
            }

            if (scene.Paced) {
                capped = true;
            }

            if (BenchScoreModel.TryGetReferenceFps(sceneName: scene.Name, referenceFps: out var referenceFps) && (scene.SceneFps > 0.0)) {
                terms.Add(item: (scene.SceneFps, referenceFps, scene.Weight));
            } else {
                // A weight>0 scene that measured zero throughput or has no reference constant is SILENTLY absent from the
                // composite — flag the whole outcome partial so a reader never mistakes a short-changed overall for a full one.
                partial = true;
            }
        }

        return new BenchRunOutcome(
            DurationSeconds: Stopwatch.GetElapsedTime(startingTimestamp: plan.StartTimestamp).TotalSeconds,
            Failure: failure,
            IncludeSamples: plan.IncludeSamples,
            Scenes: m_legScenes.ToArray(),
            Score: new BenchOverallScore(
                Capped: capped,
                Overall: BenchScoreModel.Overall(terms: terms),
                Partial: partial,
                Reference: BenchScoreModel.ReferenceLabel,
                ScoreFormula: BenchScoreModel.ScoreFormula
            ),
            StartedAtUtc: plan.StartedAtUtc,
            Suite: plan.Suite,
            SwitchName: leg.SwitchName,
            SwitchValue: leg.SwitchValue
        );
    }

    // Restores the switch snapshot verbatim and returns GPU timing to its pre-run armed state. Restores ONLY what this
    // run actually captured: a run that never reached StepArm has a null snapshot (nothing was forced), so switch
    // restore is skipped — the timing restore stays keyed off m_plan.PriorArmed, which every plan captures at creation.
    private void RestoreEnvironment() {
        if ((m_snapshot is { } snapshot) && (m_owner.Switches is { } switches)) {
            switches.Restore(snapshot: snapshot);
        }

        if (m_plan is { } plan) {
            GpuTimingControl.Shared.SetArmed(armed: plan.PriorArmed);
        }

        // Clear the captured snapshot so a SUBSEQUENT run's abort BEFORE its own StepArm (the raced-start path in
        // OnFramePublished, or an abort caught while state==Arm) can never restore THIS run's now-stale switch values.
        m_snapshot = null;
    }

    // Sets present.rate=display through the registry (the harness speaks only the switch vocabulary).
    private static void ForcePresentDisplay(FeatureSwitchRegistry switches) {
        if (switches.TryGet(name: "present.rate", descriptor: out var descriptor)) {
            _ = descriptor.Set("display");
        }
    }

    // Applies a sweep leg's switch value (RebuildRequired switches are legal — the warm frames absorb the rebuild).
    // Returns true when the override took effect (or the leg carries none, the plain-run case); false when the switch
    // REJECTED the value, so StepLegBegin can abort rather than measure a mislabeled leg.
    private bool ApplyLegSwitch(RunLeg leg) {
        if ((leg.SwitchName is not { } name) || (leg.SwitchValue is not { } value)) {
            return true;
        }

        return ((m_owner.Switches is { } switches) && switches.TryGet(name: name, descriptor: out var descriptor) && descriptor.Set(arg: value));
    }

    // Finds the beam pass index in the source's label order (the DVFS canary tracks it); -1 when absent.
    private int FindBeamPass() {
        var labels = ((m_owner.TimingSource is { } source) ? source.PassLabels : default);

        for (var index = 0; (index < labels.Length); index++) {
            if (string.Equals(a: labels[index], b: "beam", comparisonType: StringComparison.Ordinal)) {
                return index;
            }
        }

        return -1;
    }

    // Submits a scene's console script through the attached console seam (a no-op when nothing is attached).
    private void SubmitScript(IReadOnlyList<string> lines) {
        if (m_owner.SubmitLine is not { } submit) {
            return;
        }

        for (var index = 0; (index < lines.Count); index++) {
            submit(obj: lines[index]);
        }
    }
}
