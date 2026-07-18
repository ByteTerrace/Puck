using Puck.Abstractions.Gpu;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.Bench;

/// <summary>
/// The benchmark harness's DI-singleton ATTACH SURFACE — a frame-driven observer, not a render node, so the composition
/// root wires it explicitly rather than through the inherited/held capability model. A host attaches the neutral
/// seams (a GPU pass-timing source, the CPU frame-timing hub, the feature-switch registry, a console line submitter)
/// and registers the content-side scenes; the harness never references a backend, the SDF VM, or the demo. A host that
/// attaches nothing gets <c>bench.list</c> reporting an empty suite and <c>bench.run</c> refusing loudly — the harness
/// degrades honestly.
/// <para>
/// The result-sink seam is three events plus <see cref="LastOutcome"/>:
/// <see cref="SceneCompleted"/> fires as each scene finishes (the stdout table prints a scene block immediately),
/// <see cref="RunCompleted"/> fires once per run/sweep-leg with the complete in-memory <see cref="BenchRunOutcome"/>
/// (the JSON file + final block), and <see cref="SweepCompleted"/> fires after a sweep's last leg with the collected
/// legs. All fire synchronously on the render thread; a scene handler must be tiny, while a run/sweep handler runs once
/// at end-of-run and may do the heavier report I/O.
/// </para>
/// </summary>
public sealed class BenchRuntime {
    private readonly List<BenchSceneDescriptor> m_scenes = [];
    private readonly HashSet<string> m_sceneNames = new(comparer: StringComparer.Ordinal);
    private readonly BenchRunner m_runner;
    private IPassTimingSource? m_timingSource;
    private FrameTimingHub? m_frameTiming;
    private FeatureSwitchRegistry? m_switches;
    private Action<string>? m_submitLine;
    private BenchHostInfo m_hostInfo = BenchHostInfo.Detect();

    /// <summary>The standard suite name for the full ordered set of registered scenes.</summary>
    public const string StandardSuite = "standard";

    /// <summary>Creates the harness and attaches its console and JSON report sinks. A host that does not call
    /// <see cref="AttachHostInfo"/> receives a complete report whose host-specific fields remain
    /// <see cref="BenchHostInfo.Unknown"/>.</summary>
    public BenchRuntime() {
        m_runner = new BenchRunner(owner: this);

        SceneCompleted += BenchConsoleFormatter.WriteScene;
        RunCompleted += OnRunCompletedWriteReports;
        SweepCompleted += BenchConsoleFormatter.WriteSweep;
    }

    /// <summary>Fires as each scene completes, with that scene's full result — a report sink prints its stdout block
    /// immediately. Synchronous on the render thread; keep the handler tiny.</summary>
    public event Action<BenchSceneResult>? SceneCompleted;
    /// <summary>Fires once per completed run (and once per sweep leg) with the complete in-memory outcome — a report
    /// sink writes the JSON file and the final stdout block. Fires with a failure reason and empty scenes on a refused
    /// or aborted run. Synchronous on the render thread at end-of-run.</summary>
    public event Action<BenchRunOutcome>? RunCompleted;
    /// <summary>Fires after a sweep's last leg with every leg's outcome — a report sink prints the combined sweep
    /// summary table. Synchronous on the render thread.</summary>
    public event Action<BenchSweepOutcome>? SweepCompleted;

    /// <summary>The most recent completed run's outcome (a scored run, a refusal, or an abort), or
    /// <see langword="null"/> before the first run.</summary>
    public BenchRunOutcome? LastOutcome { get; private set; }

    /// <summary>The host facts written to the report's <c>engine</c> and <c>host</c> blocks; process-detected defaults until a
    /// composition root calls <see cref="AttachHostInfo"/>.</summary>
    public BenchHostInfo HostInfo => m_hostInfo;

    /// <summary>The registered scenes, in registration order.</summary>
    public IReadOnlyList<BenchSceneDescriptor> Scenes => m_scenes;

    /// <summary>Whether a run is currently latched or executing.</summary>
    public bool IsRunning => m_runner.IsRunning;

    /// <summary>Whether the present cadence is assumed CAPPED (in-session vsync) for the run — the default. A headless
    /// twin that boots an immediate-mode swapchain sets this <see langword="false"/> so the report is not stamped
    /// <c>paced</c>; an official score comes only from the uncapped twin.</summary>
    public bool AssumePacedPresent { get; set; } = true;

    // The seam accessors the runner reads (same assembly).
    internal IPassTimingSource? TimingSource => m_timingSource;
    internal Action<string>? SubmitLine => m_submitLine;
    internal FeatureSwitchRegistry? Switches => m_switches;

    /// <summary>Attaches the GPU pass-timing source the harness reads per sampled frame. Without it, a run refuses
    /// loudly.</summary>
    /// <param name="source">The neutral per-pass timing read contract.</param>
    public void AttachTimingSource(IPassTimingSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_timingSource = source;
    }

    /// <summary>Attaches the CPU frame-timing hub that drives the state machine — the harness subscribes its per-frame
    /// step to the hub's publish. Attaching a second hub replaces the subscription.</summary>
    /// <param name="hub">The launcher's frame-timing publish hub.</param>
    public void AttachFrameTiming(FrameTimingHub hub) {
        ArgumentNullException.ThrowIfNull(argument: hub);

        if (m_frameTiming is { } previous) {
            previous.Published -= m_runner.OnFramePublished;
        }

        m_frameTiming = hub;
        hub.Published += m_runner.OnFramePublished;
    }

    /// <summary>Attaches the feature-switch registry the harness snapshots/forces/restores around a run and sweeps
    /// across.</summary>
    /// <param name="registry">The control-plane switch registry.</param>
    public void AttachSwitches(FeatureSwitchRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        m_switches = registry;
    }

    /// <summary>Attaches the console line submitter the harness drives a scene's setup/teardown verb scripts through —
    /// the same pipe used by interactive and scripted callers.</summary>
    /// <param name="submitLine">The console-line sink.</param>
    public void AttachConsole(Action<string> submitLine) {
        ArgumentNullException.ThrowIfNull(argument: submitLine);

        m_submitLine = submitLine;
    }

    /// <summary>Attaches the host facts a composition root actually knows (GPU name, backend, resolution, present
    /// mode/tier, render-scale tier, git commit/branch) — every field not populated by <see cref="BenchHostInfo.Detect"/>
    /// itself. Optional: a host that never calls this still gets a complete report with those fields stamped
    /// <see cref="BenchHostInfo.Unknown"/>.</summary>
    /// <param name="hostInfo">The host facts to stamp into every subsequent report.</param>
    public void AttachHostInfo(BenchHostInfo hostInfo) {
        ArgumentNullException.ThrowIfNull(argument: hostInfo);

        m_hostInfo = hostInfo;
    }

    /// <summary>Registers a content-side scene into the suite. Registration order is the run order. A duplicate name
    /// throws — a scene's identity must be unique.</summary>
    /// <param name="descriptor">The scene to register.</param>
    /// <exception cref="ArgumentException">A scene with the same name is already registered.</exception>
    public void RegisterScene(BenchSceneDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(argument: descriptor);

        if (!m_sceneNames.Add(item: descriptor.Name)) {
            throw new ArgumentException(message: $"A bench scene named '{descriptor.Name}' is already registered.", paramName: nameof(descriptor));
        }

        m_scenes.Add(item: descriptor);
    }

    /// <summary>Starts a scored run of the suite (the <c>bench.run</c> entry point). Refuses loudly — without starting —
    /// when no timing source is attached, the suite is unknown or empty, or no frame-timing hub can drive it.</summary>
    /// <param name="suite">The suite name (defaults, at the call site, to <see cref="StandardSuite"/>).</param>
    /// <param name="includeSamples">Whether each scene retains its raw per-frame wall samples for a raw dump.</param>
    /// <returns>A status line for the console.</returns>
    public string StartRun(string suite, bool includeSamples) {
        if (Refuse(suite: suite, scenes: out var scenes) is { } refusal) {
            return refusal;
        }

        var plan = BenchRunner.PlanRun(suite: suite, scenes: scenes, includeSamples: includeSamples);

        if (!m_runner.RequestRun(plan: plan)) {
            return "[bench: a run is already in progress — bench.abort to stop it]";
        }

        return $"[bench: started '{suite}' — {scenes.Count} scene(s); results stream as each scene completes]";
    }

    /// <summary>Starts a sweep of the suite once per value of a switch (the <c>bench.sweep</c> entry point), snapshotting
    /// and restoring around the whole sweep. Refuses loudly when a prerequisite is missing.</summary>
    /// <param name="switchName">The switch to sweep.</param>
    /// <param name="values">The values to sweep, in order.</param>
    /// <param name="suite">The suite each leg runs.</param>
    /// <returns>A status line for the console.</returns>
    public string StartSweep(string switchName, IReadOnlyList<string> values, string suite) {
        if (m_switches is not { } switches) {
            return "[bench.sweep: refused — no feature-switch registry attached]";
        }

        if (!switches.TryGet(name: switchName, descriptor: out var descriptor)) {
            return $"[bench.sweep: unknown switch '{switchName}' — feature.list shows what can be swept]";
        }

        if (values.Count == 0) {
            return $"[bench.sweep: give at least one value — bench.sweep {switchName}=<v1,v2,...>]";
        }

        // Validate EVERY value against the switch's allowed set up front and refuse the WHOLE sweep on any unknown value
        // — a typo (e.g. 'natve') would otherwise start the sweep and label a leg with a value that never applied,
        // labeling a leg with a value that was not applied.
        foreach (var value in values) {
            if (!descriptor.AllowedValues.Contains(value: value, comparer: StringComparer.Ordinal)) {
                return $"[bench.sweep: '{value}' is not a valid value for '{switchName}' — allowed: {string.Join(separator: '/', values: descriptor.AllowedValues)}]";
            }
        }

        if (Refuse(suite: suite, scenes: out var scenes) is { } refusal) {
            return refusal;
        }

        var plan = BenchRunner.PlanSweep(suite: suite, scenes: scenes, switchName: switchName, values: values);

        if (!m_runner.RequestRun(plan: plan)) {
            return "[bench: a run is already in progress — bench.abort to stop it]";
        }

        return $"[bench.sweep: started '{switchName}' × {values.Count} over '{suite}' — one report per value]";
    }

    /// <summary>Requests that the active run abort (the <c>bench.abort</c> entry point).</summary>
    /// <returns>A status line for the console.</returns>
    public string Abort() => m_runner.RequestAbort();

    // The shared refusal gate for run/sweep: null when the run may start, otherwise the loud refusal line.
    private string? Refuse(string suite, out IReadOnlyList<BenchSceneDescriptor> scenes) {
        scenes = [];

        if (m_timingSource is null) {
            return "[bench: refused — no GPU pass-timing source attached (cannot read timestamps honestly)]";
        }

        if (m_frameTiming is null) {
            return "[bench: refused — no frame-timing hub attached (the run cannot be driven)]";
        }

        if (!string.Equals(a: suite, b: StandardSuite, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return $"[bench: unknown suite '{suite}' — try '{StandardSuite}']";
        }

        if (m_scenes.Count == 0) {
            return "[bench: refused — no scenes registered]";
        }

        scenes = m_scenes.ToArray();

        return null;
    }

    // The always-on JSON sink + its stdout companion — wired once, in the constructor, ahead of any caller-attached
    // RunCompleted handler, so the report line's path is available to print in the SAME stdout block.
    private void OnRunCompletedWriteReports(BenchRunOutcome outcome) {
        var featureSwitches = (m_switches?.Snapshot().Values ?? new Dictionary<string, string>(comparer: StringComparer.Ordinal));
        string? reportPath = null;

        // A report-write failure (a locked bench-reports/ file, a full disk, a permission fault) must NEVER take the
        // launcher loop down after a clean scored run — the run's numbers are already computed and its stdout block is
        // still owed. Log one loud line and continue; the stdout table below prints with no report path.
        try {
            reportPath = BenchReportWriter.Write(featureSwitches: featureSwitches, host: m_hostInfo, outcome: outcome);
        } catch (Exception exception) when (((exception is IOException) || (exception is UnauthorizedAccessException) || (exception is SystemException))) {
            Console.Error.WriteLine(value: $"[bench] REPORT WRITE FAILED {BenchReportWriter.ReportDirectoryName}: {exception.Message}");
        }

        BenchConsoleFormatter.WriteRun(host: m_hostInfo, outcome: outcome, reportPath: reportPath);
    }

    // The runner raises results back through these (same assembly).
    internal void RaiseSceneCompleted(BenchSceneResult result) => SceneCompleted?.Invoke(obj: result);
    internal void RaiseRunCompleted(BenchRunOutcome outcome) {
        LastOutcome = outcome;
        RunCompleted?.Invoke(obj: outcome);
    }
    internal void RaiseSweepCompleted(BenchSweepOutcome sweep) => SweepCompleted?.Invoke(obj: sweep);
}
