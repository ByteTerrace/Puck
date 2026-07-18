using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Pacing;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.Commands;
using Puck.Hosting;
using Puck.Input;

namespace Puck.Launcher;

/// <summary>
/// The outermost host loop — the dumb terminal. It owns the window + swapchain, and each frame drives the
/// single primary <see cref="IRenderNode"/> to produce one surface, then blits that surface to the
/// swapchain. The terminal knows nothing about the world — only the one surface the engine hands up. The
/// engine drives the terminal's lifecycle through the baton it was handed on the root host context; this
/// loop merely drains the resulting exit request (and honors <c>--exit-after</c> for scripted runs).
/// </summary>
public sealed class LauncherWindowHostedService : BackgroundService {
    // Below this much time remaining the pacer busy-waits for an accurate wake-up; above it, it sleeps.
    private const int SpinThresholdMilliseconds = 2;
    // Fixed simulation rate (Hz); a divisor of EngineTicks.PerSecond so the step is a whole number of ticks.
    private const uint TargetUpdateRate = 240U;
    // Cap on back-to-back device-loss recoveries with no successful frame between them, so a permanently-dead GPU (or a
    // presenter that cannot recover) fails loudly instead of spinning forever. Reset to 0 after any good frame.
    private const int MaxConsecutiveDeviceLossRecoveries = 8;
    // A real device loss (driver crash/update, the adapter disabled/removed) leaves NO capable adapter for SECONDS: the
    // fresh device create keeps failing until it returns. Recovery waits out that window — retrying the rebuild with this
    // backoff for up to this budget — before giving up. These waits are ONE loss's recovery, so they do NOT advance the
    // consecutive-loss streak above (which guards against a device that drops again the instant it is recovered).
    private const int DeviceReacquireBackoffMilliseconds = 250;
    private const double DeviceReacquireBudgetSeconds = 10.0;
    // [frame-timing] digest cadence — summarize each block of produced frames, matching SdfEngineNode's
    // [world-timing] throttle so the two digests read at the same rate.
    private const ulong FrameTimingReportInterval = 60UL;

    private readonly IHostApplicationLifetime m_applicationLifetime;
    private readonly BufferedConsoleOutput m_bufferedOutput;
    private readonly ExternalClockRegistry m_externalClocks;
    private readonly FrameTimingHub m_frameTimingHub;
    private readonly IInputClock m_inputClock;
    private readonly InputRouter? m_inputRouter;
    private readonly ILogger<LauncherWindowHostedService> m_logger;
    private readonly LauncherOptions m_options;
    private readonly PresentPacingControl m_presentPacing;
    private readonly ISurfacePresenter m_presenter;
    private readonly IRenderNode m_root;
    private readonly IHostContext m_rootHostContext;
    private readonly CommandShell m_shell;
    private readonly CommandRegistry m_registry;
    private readonly IFixedStepSimulation? m_simulation;
    private readonly ISnapshotInputCapture[] m_snapshotInputCaptures;
    private readonly TerminalControl m_terminal;
    private readonly INativeWindowFactory m_windowFactory;
    private ulong m_frameTimingDigestLastProducedFrameIndex;
    private ulong m_frameTimingDigestSampleCount;
    private FrameTimingSample m_frameTimingDigestWorst;

    public LauncherWindowHostedService(
        IHostApplicationLifetime applicationLifetime,
        BufferedConsoleOutput bufferedOutput,
        ExternalClockRegistry externalClocks,
        FrameTimingHub frameTimingHub,
        IInputClock inputClock,
        ILogger<LauncherWindowHostedService> logger,
        LauncherOptions options,
        PresentPacingControl presentPacing,
        ISurfacePresenter presenter,
        IRenderNode root,
        IHostContext rootHostContext,
        IEnumerable<InputRouter> inputRouters,
        IEnumerable<IFixedStepSimulation> simulations,
        IEnumerable<ISnapshotInputCapture> snapshotInputCaptures,
        CommandRegistry registry,
        CommandShell shell,
        TerminalControl terminal,
        INativeWindowFactory windowFactory
    ) {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(bufferedOutput);
        ArgumentNullException.ThrowIfNull(externalClocks);
        ArgumentNullException.ThrowIfNull(frameTimingHub);
        ArgumentNullException.ThrowIfNull(inputClock);
        ArgumentNullException.ThrowIfNull(inputRouters);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(presentPacing);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootHostContext);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(windowFactory);

        m_applicationLifetime = applicationLifetime;
        m_bufferedOutput = bufferedOutput;
        m_externalClocks = externalClocks;
        m_frameTimingHub = frameTimingHub;
        m_inputClock = inputClock;
        m_inputRouter = SingleOrDefault(items: inputRouters, name: nameof(InputRouter));
        m_logger = logger;
        m_options = options;
        m_presentPacing = presentPacing;
        m_presenter = presenter;
        m_root = root;
        m_rootHostContext = rootHostContext;
        m_registry = registry;
        m_shell = shell;
        m_simulation = SingleOrDefault(items: simulations, name: nameof(IFixedStepSimulation));
        m_snapshotInputCaptures = snapshotInputCaptures.ToArray();
        m_terminal = terminal;
        m_windowFactory = windowFactory;

        if ((m_simulation is null) != (m_inputRouter is null)) {
            throw new InvalidOperationException(message: "A fixed-step simulation and its InputRouter must be registered together. Use AddFixedStepSimulation<TSimulation>().");
        }

        m_registry.RouteSimulationTo(sink: m_inputRouter);
    }

    private static T? SingleOrDefault<T>(IEnumerable<T> items, string name)
        where T : class {
        using var enumerator = items.GetEnumerator();

        if (!enumerator.MoveNext()) {
            return null;
        }

        var item = enumerator.Current;

        if (enumerator.MoveNext()) {
            throw new InvalidOperationException(message: $"The launcher accepts at most one {name}.");
        }

        return item;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) {
        var pumpThread = new Thread(start: () => RunWindowLoop(stoppingToken: stoppingToken)) {
            IsBackground = true,
            Name = "Puck.Launcher Window Pump",
        };

        pumpThread.Start();

        return Task.CompletedTask;
    }

    private void RunWindowLoop(CancellationToken stoppingToken) {
        try {
            using var window = m_windowFactory.Create();

            try {
                if (window is not IWindowInputSource inputSource) {
                    throw new InvalidOperationException(message: "The launcher requires a window that can provide input.");
                }

                m_presenter.Activate(
                    binding: window.CreateSurfaceBinding(),
                    height: window.Height,
                    width: window.Width
                );

                if (m_logger.IsEnabled(logLevel: LogLevel.Information)) {
                    m_logger.LogInformation(
                        "Opened native window \"{Title}\" ({Width}x{Height}); hosting the primary engine.",
                        window.Title,
                        window.Width,
                        window.Height
                    );
                }

                window.Show();

                var accumulatorTicks = 0UL;
                var clock = TickClock.Start();
                var captureOriginTicks = m_inputClock.NowTicks;
                var elapsedTicks = 0UL;
                var hostFrame = 0UL;
                var frequency = Stopwatch.Frequency;
                var maxFrameTicks = (EngineTicks.PerSecond / 4UL);
                // Display-aware presentation pacing. Active signal timing and explicit VRR capabilities are independent
                // facts; the host never turns selectable fixed modes into a fictional VRR range. Re-resolve only when the
                // window reports a display/topology change.
                var displayTimingInfo = (window as IDisplayTimingInfo);
                var displayTiming = (displayTimingInfo?.QueryDisplayTiming() ?? DisplayTimingSnapshot.Unknown);
                var displayConfigurationVersion = (displayTimingInfo?.DisplayConfigurationVersion ?? 0UL);
                const int displayTimingRetryLimit = 8;
                var displayTimingRetryAttemptsRemaining = (displayTimingInfo is not null && !displayTiming.IsKnown ? displayTimingRetryLimit : 0);
                var nextDisplayTimingRetryTimestamp = 0L;
                var precisionWaiter = (window as IPrecisionWaiter);
                // An optional HELD root capability (contributed by the composition root, e.g. the demo's
                // PointerStore) that wants every raw pointer event as it is dequeued — see IPointerInputSink's
                // doc comment for why pointer/button state bypasses the InputSignal/command-binding pipeline
                // below entirely. Resolved once: the set of contributed capabilities never changes mid-run.
                _ = m_rootHostContext.HoldsCapability<IPointerInputSink>(capability: out var pointerSink);
                // CLOSED-LOOP present timing (VK_KHR_present_wait): the presenter confirms each present and reports the
                // instant it was confirmed. The pacer OBSERVES this rhythm — reporting the measured display interval (the
                // DELTA between consecutive confirmed presents, the only phase-meaningful part of the sample) — but it does
                // NOT re-anchor the render deadline to the confirmation timestamp. That timestamp is backend-specific and,
                // for Vulkan, is the CPU instant vkWaitForPresentKHR returned INSIDE this frame's Present call — i.e. AFTER
                // produce ran — so anchoring the deadline to it and adding the period pushed every deadline out by one whole
                // produce, serializing produce and the pacer wait (capping ~120 Hz runs near ~100 FPS). The deadline is
                // instead advanced on an absolute slot grid (see the pacer block below), which lets produce + GPU work
                // overlap the wait. Absent any feedback the pacer is unaffected. Render-side only — never touches the sim.
                var presentTiming = (m_presenter as IPresentTimingFeedback);
                var lastObservedPresentCount = 0u;
                var previousPresentTimestamp = 0L;
                var presentSampleCounter = 0;
                // Opt-in through LauncherOptions.LogPresentTiming: periodically log the
                // measured present interval — proof the closed loop is live and what the real display cadence is. Off
                // by default so a shipped run isn't noisy.
                var logPresentTiming = m_options.LogPresentTiming;
                // GENLOCK (latency phase-align): when an external frame producer (a live camera) publishes arrival
                // timestamps, the aligner biases the render deadline toward them with a light PI filter on the phase
                // error, so the frame that samples a fresh arrival starts (and presents) as soon after it as possible —
                // full VRR rate preserved, the fixed-step sim untouched. Silent with no publisher;
                // LauncherOptions.GenlockEnabled disables it.
                var genlock = new GenlockPhaseAligner(clock: m_externalClocks.PacerClock, enabled: m_options.GenlockEnabled, logger: m_logger, logPhase: logPresentTiming);
                // Starts behind any possible registry state so the first loop iteration always evaluates (and, when
                // sources registered before the loop, announces) the current election.
                var observedElectionGeneration = -1;
                // The live present-rate target the `present-rate` verb retargets: the pacer re-resolves its period when
                // this control's version advances (mirroring the display-change re-resolve below). Presentation only —
                // never reaches the fixed-step sim.
                var presentPacingVersion = m_presentPacing.Version;
                var renderPeriod = ResolveRenderPeriod(displayTiming: displayTiming, frequency: frequency, requestedHertz: m_presentPacing.TargetHertz);
                var spinThreshold = ((frequency / 1000L) * SpinThresholdMilliseconds);
                var stepTicks = EngineTicks.PerRate(ratePerSecond: TargetUpdateRate);
                var startTimestamp = Stopwatch.GetTimestamp();
                var nextRenderDeadline = startTimestamp;
                var exitAfterTimestamp = ((m_options.ExitAfter is { } exitAfter)
                    ? (startTimestamp + (long)(exitAfter.TotalSeconds * frequency))
                    : (long?)null);
                // Consecutive device-loss recoveries with no good frame in between; bounded so a permanently-dead GPU
                // (or a backend that can't recover) surfaces the failure instead of spinning forever.
                var deviceLossStreak = 0;
                // Test hook: a one-shot synthetic device loss N seconds in, to exercise recovery without real GPU churn.
                var syntheticDeviceLossAt = ResolveSyntheticDeviceLossTimestamp(seconds: m_options.SyntheticDeviceLossSeconds, startTimestamp: startTimestamp, frequency: frequency);
                var syntheticDeviceLossFired = false;

                // [frame-timing] (presentation-side only — Stopwatch ticks, never simulation state): wall buckets around
                // the loop's own phases, tiling the loop-top-to-loop-top interval so bucket sums plus the remainder
                // always equal the measured interval. An optional IPresentationSkipFeedback presenter (Vulkan) folds its
                // running skipped-present tally into the same line. Arming is the live GpuTimingControl.Shared state (a
                // bench arm / the demo's gpu.timing switch / Puck.World's world.timing verb flip it mid-session, and the
                // run-doc host.timing field seeds it) — so ONE switch lights both the GPU per-pass digest and this CPU
                // hub. Each armed iteration publishes a sample into the frame-timing hub, and the throttled stderr digest
                // is one SUBSCRIBER of that hub rather than a private code path — the bench runner is another.
                var frameTimingSkipFeedback = (m_presenter as IPresentationSkipFeedback);
                var frameTimingProducedFrames = 0UL;

                m_frameTimingHub.Published += PublishFrameTimingDigest;

                while (
                    window.IsOpen &&
                    !stoppingToken.IsCancellationRequested
                ) {
                    // Re-read the live arming state each iteration so a mid-session arm/disarm (bench.run, gpu.timing)
                    // takes effect without a restart.
                    var frameTimingEnabled = GpuTimingControl.Shared.Armed;
                    // The loop-top mark: [frame-timing]'s interval bucket is THIS iteration's own span (loop-top to the
                    // point just before the next loop-top re-check below), so every bucket measured inside this iteration
                    // tiles it exactly.
                    var frameTimingIterationStart = (frameTimingEnabled ? Stopwatch.GetTimestamp() : 0L);
                    var frameTimingGcPauseStart = (frameTimingEnabled ? GC.GetTotalPauseDuration().Ticks : 0L);
                    var frameTimingGcCollectionsStart = (frameTimingEnabled
                        ? ((GC.CollectionCount(generation: 0) + GC.CollectionCount(generation: 1)) + GC.CollectionCount(generation: 2))
                        : 0);
                    var frameTimingPumpTicks = 0L;
                    var frameTimingClockTicks = 0L;
                    var frameTimingInputSnapshotTicks = 0L;
                    var frameTimingCommandApplyTicks = 0L;
                    var frameTimingSimulationStepTicks = 0L;
                    var frameTimingFixedStepOverheadTicks = 0L;
                    var frameTimingFixedSteps = 0UL;
                    var frameTimingSimulationOutputTicks = 0L;
                    var frameTimingBeginFrameTicks = 0L;
                    var frameTimingProduceTicks = 0L;
                    var frameTimingPresentTicks = 0L;
                    var frameTimingPostPresentTicks = 0L;
                    var frameTimingPacerTicks = 0L;

                    window.PollEvents();

                    // PollEvents may have processed a display change. Immediately discard both old-monitor facts, then
                    // make bounded retries because Windows topology queries can be transiently unavailable mid-change.
                    if (
                        (displayTimingInfo is not null) &&
                        (displayTimingInfo.DisplayConfigurationVersion != displayConfigurationVersion)
                    ) {
                        displayConfigurationVersion = displayTimingInfo.DisplayConfigurationVersion;
                        displayTiming = DisplayTimingSnapshot.Unknown;
                        renderPeriod = ResolveRenderPeriod(displayTiming: displayTiming, frequency: frequency, requestedHertz: m_presentPacing.TargetHertz);
                        displayTimingRetryAttemptsRemaining = displayTimingRetryLimit;
                        nextDisplayTimingRetryTimestamp = 0L;
                    }

                    if (
                        (displayTimingInfo is not null) &&
                        (displayTimingRetryAttemptsRemaining > 0) &&
                        (Stopwatch.GetTimestamp() >= nextDisplayTimingRetryTimestamp)
                    ) {
                        var requeriedTiming = displayTimingInfo.QueryDisplayTiming();

                        --displayTimingRetryAttemptsRemaining;
                        displayTiming = requeriedTiming;
                        renderPeriod = ResolveRenderPeriod(displayTiming: displayTiming, frequency: frequency, requestedHertz: m_presentPacing.TargetHertz);

                        if (requeriedTiming.IsKnown) {
                            displayTimingRetryAttemptsRemaining = 0;
                        } else {
                            nextDisplayTimingRetryTimestamp = (Stopwatch.GetTimestamp() + (frequency / 10L));
                        }
                    }

                    // A live present-rate retarget (the `present-rate` verb) bumps the control's version; re-resolve the
                    // pacer period against the current display range so the new cadence takes effect next frame. The pacer
                    // deadline below re-anchors naturally (the catch-up clamp absorbs the transition). Presentation-side
                    // only — the fixed-step sim is untouched.
                    if (m_presentPacing.Version != presentPacingVersion) {
                        presentPacingVersion = m_presentPacing.Version;
                        renderPeriod = ResolveRenderPeriod(displayTiming: displayTiming, frequency: frequency, requestedHertz: m_presentPacing.TargetHertz);
                    }

                    // GENLOCK election watch: announce (once per election change) when plural rhythm sources are
                    // registered with no election, so the resulting silent free-run is visible to the operator.
                    NoteExternalClockContention(observedElectionGeneration: ref observedElectionGeneration);

                    // When the window is not focused, key/button releases are not delivered, so drop any held
                    // inputs — otherwise a value down at the moment focus was lost would stay stuck.
                    if (
                        m_rootHostContext.HoldsCapability<IInputFocus>(capability: out var heldFocus) &&
                        !heldFocus.IsActiveFor(deviceId: default)
                    ) {
                        m_shell.ReleaseHeld();
                        m_inputRouter?.ReleaseHeld();
                    }

                    m_shell.BeginFrame();

                    while (inputSource.TryDequeueInput(inputEvent: out var windowInput)) {
                        // Hand the RAW event to the pointer sink first, unconditionally (not focus-gated): pointer
                        // state is presentation/session-only, never simulation input, so it never touches
                        // CaptureTick/CommandSnapshot below — it just mirrors the window's own button/position
                        // truth for whichever composition root wants it (e.g. a draggable overlay panel).
                        pointerSink?.Observe(inputEvent: in windowInput);

                        // Stamp at the pump: the wndproc dispatched these during PollEvents above, so capture
                        // time ≈ now. Monotonic and sufficient to attribute the input to a fixed-step tick;
                        // per-event OS-event-time (GetMessageTime via OsTimeCorrelator) is a later refinement.
                        var signal = WindowInputMapper.ToInputSignal(inputEvent: in windowInput) with {
                            CaptureTick = m_inputClock.NowTicks,
                        };

                        if (
                            m_rootHostContext.HoldsCapability<IInputFocus>(capability: out var inputFocus) &&
                            inputFocus.IsActiveFor(deviceId: signal.DeviceId)
                        ) {
                            if (m_inputRouter is { } router) {
                                router.Capture(signal: in signal);
                            } else {
                                m_shell.Enqueue(signal: signal);
                            }
                        }
                    }

                    for (var captureIndex = 0; (captureIndex < m_snapshotInputCaptures.Length); captureIndex++) {
                        m_snapshotInputCaptures[captureIndex].CaptureFrame(frameKey: hostFrame);
                    }

                    hostFrame++;

                    m_shell.Collect();

                    // Flush the command pump's buffered result echoes ONCE, right after the drain that produced them
                    // (see BufferedConsoleOutput): every line submitted this frame appended to the buffer during Collect,
                    // so one flush here emits the whole burst in a single write, preserving FIFO order. The teardown
                    // finally-block flushes again so the final lines before an --exit-after shutdown are never lost.
                    m_bufferedOutput.Flush();

                    // [frame-timing] pump bucket: everything from loop-top through the input drain above (PollEvents,
                    // the display/genlock/focus checks, the windowInput dequeue loop, Collect).
                    if (frameTimingEnabled) {
                        frameTimingPumpTicks = (Stopwatch.GetTimestamp() - frameTimingIterationStart);
                    }

                    var frameTimingClockStart = (frameTimingEnabled ? Stopwatch.GetTimestamp() : 0L);

                    if (
                        (exitAfterTimestamp is { } deadline) &&
                        (Stopwatch.GetTimestamp() >= deadline)
                    ) {
                        m_terminal.RequestExit();
                    }

                    var deltaTicks = clock.Sample();

                    if (deltaTicks > maxFrameTicks) {
                        // InputClock never clamps, while the simulation intentionally drops excess wall time. Rebase the
                        // capture-to-simulation pin by the dropped interval so newly captured input remains due now rather
                        // than waiting for simulation time the host deliberately discarded.
                        captureOriginTicks += (deltaTicks - maxFrameTicks);
                        deltaTicks = maxFrameTicks;
                    }

                    accumulatorTicks += deltaTicks;

                    var consumedTicks = ((accumulatorTicks / stepTicks) * stepTicks);

                    accumulatorTicks -= consumedTicks;
                    var previousElapsedTicks = elapsedTicks;

                    elapsedTicks += consumedTicks;

                    if (frameTimingEnabled) {
                        frameTimingClockTicks = (Stopwatch.GetTimestamp() - frameTimingClockStart);
                    }

                    var frameTimingFixedStepStart = (frameTimingEnabled ? Stopwatch.GetTimestamp() : 0L);

                    if ((m_simulation is { } simulation) && (m_inputRouter is { } inputRouter)) {
                        var stepCount = (consumedTicks / stepTicks);
                        var firstTick = (previousElapsedTicks / stepTicks);

                        for (var stepIndex = 0UL; (stepIndex < stepCount); stepIndex++) {
                            var tick = (firstTick + stepIndex);
                            var stepElapsedTicks = ((tick + 1UL) * stepTicks);
                            var windowEndTick = (captureOriginTicks + stepElapsedTicks);
                            var frameTimingInputSnapshotStart = (frameTimingEnabled ? Stopwatch.GetTimestamp() : 0L);
                            var commands = inputRouter.SnapshotForTick(tick: tick, windowEndTick: windowEndTick);

                            if (frameTimingEnabled) {
                                frameTimingInputSnapshotTicks += (Stopwatch.GetTimestamp() - frameTimingInputSnapshotStart);
                            }

                            var frameTimingCommandApplyStart = (frameTimingEnabled ? Stopwatch.GetTimestamp() : 0L);

                            m_registry.ApplySnapshot(snapshot: in commands);

                            if (frameTimingEnabled) {
                                frameTimingCommandApplyTicks += (Stopwatch.GetTimestamp() - frameTimingCommandApplyStart);
                            }

                            var fixedStep = new FixedStepContext(
                                ElapsedTicks: stepElapsedTicks,
                                StepTicks: stepTicks,
                                Tick: tick
                            );
                            var frameTimingSimulationStepStart = (frameTimingEnabled ? Stopwatch.GetTimestamp() : 0L);

                            simulation.Step(context: in fixedStep, commands: in commands);

                            if (frameTimingEnabled) {
                                frameTimingSimulationStepTicks += (Stopwatch.GetTimestamp() - frameTimingSimulationStepStart);
                                frameTimingFixedSteps++;
                            }
                        }
                    }

                    if (frameTimingEnabled) {
                        var frameTimingFixedStepTicks = (Stopwatch.GetTimestamp() - frameTimingFixedStepStart);

                        frameTimingFixedStepOverheadTicks = (((frameTimingFixedStepTicks
                            - frameTimingInputSnapshotTicks)
                            - frameTimingCommandApplyTicks)
                            - frameTimingSimulationStepTicks);
                    }

                    var frameTimingSimulationOutputStart = (frameTimingEnabled ? Stopwatch.GetTimestamp() : 0L);

                    // Simulation-routed console handlers run while snapshots are applied above. Flush their real
                    // results in this iteration rather than leaving them buffered until the next rendered frame.
                    m_bufferedOutput.Flush();

                    var width = window.Width;
                    var height = window.Height;

                    if (frameTimingEnabled) {
                        frameTimingSimulationOutputTicks = (Stopwatch.GetTimestamp() - frameTimingSimulationOutputStart);
                    }

                    var frameTimingPostPresentStart = 0L;

                    // The frame body (present-side GPU work) can surface a DEVICE LOST — DXGI_ERROR_DEVICE_REMOVED /
                    // VK_ERROR_DEVICE_LOST — at BeginFrame's wait-for-idle, the node tree's own submit, or Present, all
                    // translated to a neutral DeviceLostException at the backend boundary. Catch it here, recover the
                    // device + resources, and resume. The fixed-step sim above is already advanced for this tick and is
                    // NOT touched — a recovery that burns several wall-clock frames is absorbed by the maxFrameTicks clamp,
                    // so a recorded run produces identical sim ticks regardless of recovery hitches.
                    try {
                        // Test hook (PUCK_TEST_DEVICE_LOSS=<seconds>): inject ONE synthetic device loss to exercise the
                        // full recovery path (catch -> node reset -> device recreate -> resume) on a HEALTHY GPU — no
                        // driver reset, no black-screen risk. Validates the rebuild machinery; the real native-detection
                        // path is exercised separately by a true loss (e.g. Win+Ctrl+Shift+B).
                        ThrowIfSyntheticDeviceLossDue(at: syntheticDeviceLossAt, fired: ref syntheticDeviceLossFired);

                        // BeginFrame recreates presentation resources when the size changed and waits for the
                        // previous frame's GPU work, so the node tree can safely reuse its per-frame resources — the
                        // [frame-timing] "gpu-drain" bucket, since that wait is where the PRIOR frame's GPU work is drained.
                        var frameTimingBeginFrameStart = (frameTimingEnabled ? Stopwatch.GetTimestamp() : 0L);

                        m_presenter.BeginFrame(
                            height: height,
                            width: width
                        );

                        if (frameTimingEnabled) {
                            frameTimingBeginFrameTicks = (Stopwatch.GetTimestamp() - frameTimingBeginFrameStart);
                        }

                        if (
                            (width > 0) &&
                            (height > 0)
                        ) {
                            var frameContext = new FrameContext(
                                AccumulatorTicks: accumulatorTicks,
                                DeltaTicks: consumedTicks,
                                ElapsedTicks: elapsedTicks,
                                FrameDeltaTicks: deltaTicks,
                                Host: m_rootHostContext,
                                StepTicks: stepTicks,
                                TargetHeight: height,
                                TargetWidth: width
                            );
                            var frameTimingProduceStart = (frameTimingEnabled ? Stopwatch.GetTimestamp() : 0L);
                            var surface = m_root.ProduceFrame(context: in frameContext);

                            if (frameTimingEnabled) {
                                frameTimingProduceTicks = (Stopwatch.GetTimestamp() - frameTimingProduceStart);
                            }

                            var frameTimingPresentStart = (frameTimingEnabled ? Stopwatch.GetTimestamp() : 0L);

                            m_presenter.Present(surface: surface);

                            if (frameTimingEnabled) {
                                frameTimingPresentTicks = (Stopwatch.GetTimestamp() - frameTimingPresentStart);
                            }

                            ++frameTimingProducedFrames;
                        }

                        frameTimingPostPresentStart = (frameTimingEnabled ? Stopwatch.GetTimestamp() : 0L);

                        NoteFrameSucceeded(streak: ref deviceLossStreak);
                    } catch (DeviceLostException deviceLost) {
                        if (!TryRecoverFromDeviceLoss(
                            binding: window.CreateSurfaceBinding(),
                            deviceLost: deviceLost,
                            height: height,
                            streak: ref deviceLossStreak,
                            width: width
                        )) {
                            // Unrecoverable (device never returned, presenter can't recover, or too many losses in a
                            // row). Shut DOWN cleanly rather than crashing: close the window and break to the normal
                            // teardown. The teardown drains tolerate the already-lost device, so no exception escapes.
                            m_logger.LogWarning(message: "Shutting down after an unrecoverable graphics device loss.");
                            window.Close();
                            window.PollEvents();

                            break;
                        }

                        // Skip this frame's present-timing/pacing work; the next iteration renders on the fresh device.
                        continue;
                    }

                    // Everything from the completed present through the pacing decision is tracked separately from the
                    // actual deadline wait. This isolates feedback/genlock/exit bookkeeping from both GPU work and slack.
                    if (frameTimingEnabled && (0L == frameTimingPostPresentStart)) {
                        frameTimingPostPresentStart = Stopwatch.GetTimestamp();
                    }

                    var frameTimingPostPresentClosed = false;

                    if (m_terminal.TryConsumeExit()) {
                        window.Close();
                        window.PollEvents();
                        break;
                    }

                    if (renderPeriod > 0L) {
                        // GRID-ANCHORED pacing. The render deadline advances by exactly renderPeriod from the PREVIOUS
                        // deadline — an absolute present-slot grid — NOT from when produce/present finished this frame. That
                        // is what lets this frame's produce + GPU work OVERLAP the wait for the next slot: the wait is only
                        // the slack between the fixed grid point and however long produce ran, so the loop-to-loop interval
                        // is the slot itself (renderPeriod), not produce + slot.
                        //
                        // The closed loop stays OBSERVED, not authoritative: when the presenter confirms a NEW present, the
                        // measured display interval (the DELTA between confirmed presents) is reported, but the confirmation
                        // timestamp does NOT move the deadline — re-anchoring to it serialized produce and the wait (that
                        // timestamp is a post-produce, one-present-lagged value on Vulkan; see the presentTiming remarks
                        // above). When no present-timing capability is present this whole block is a no-op.
                        if (presentTiming is not null) {
                            var sample = presentTiming.LastPresentTiming;

                            if (sample.IsAvailable && (sample.PresentCount != lastObservedPresentCount)) {
                                lastObservedPresentCount = sample.PresentCount;

                                // Diagnostic: the measured display-present interval between confirmed presents — throttled
                                // so it isn't noisy, and only when opted in. This is the at-a-glance "closed loop is live".
                                if (
                                    logPresentTiming &&
                                    (previousPresentTimestamp > 0L) &&
                                    (0 == (++presentSampleCounter % 120)) &&
                                    m_logger.IsEnabled(logLevel: LogLevel.Information)
                                ) {
                                    var intervalMilliseconds = (((double)(sample.PresentTimestampTicks - previousPresentTimestamp) * 1000.0) / frequency);

                                    if (intervalMilliseconds > 0.0) {
                                        m_logger.LogInformation(
                                            "Closed-loop present timing live: measured interval {Interval:0.00} ms ({Hertz:0.#} Hz).",
                                            intervalMilliseconds,
                                            (1000.0 / intervalMilliseconds)
                                        );
                                    }
                                }

                                previousPresentTimestamp = sample.PresentTimestampTicks;
                            }
                        }

                        // Advance to the next grid slot. GENLOCK biases the slot toward the latest external arrival (see
                        // GenlockPhaseAligner) when one publishes; a no-op with no publisher or a stale feed.
                        nextRenderDeadline = genlock.Apply(deadline: (nextRenderDeadline + renderPeriod), frequency: frequency, renderPeriod: renderPeriod);

                        var nowTimestamp = Stopwatch.GetTimestamp();

                        // CATCH-UP: a frame that overran its slot (GPU-bound, or a one-off hitch) is already more than a
                        // full slot past the next grid point. Re-origin the grid at now — jump to the next slot — instead
                        // of accumulating the missed slots as debt (which would burst several presents back-to-back to
                        // "catch up"). A GPU-bound frame therefore never waits: its interval is its own GPU time, unchanged.
                        if ((nowTimestamp - nextRenderDeadline) > renderPeriod) {
                            nextRenderDeadline = nowTimestamp;
                        } else {
                            if (frameTimingEnabled) {
                                frameTimingPostPresentTicks = (Stopwatch.GetTimestamp() - frameTimingPostPresentStart);
                                frameTimingPostPresentClosed = true;
                            }

                            var frameTimingPacerStart = (frameTimingEnabled ? Stopwatch.GetTimestamp() : 0L);

                            WaitUntil(
                                deadlineTimestamp: nextRenderDeadline,
                                frequency: frequency,
                                precisionWaiter: precisionWaiter,
                                spinThreshold: spinThreshold
                            );

                            if (frameTimingEnabled) {
                                frameTimingPacerTicks = (Stopwatch.GetTimestamp() - frameTimingPacerStart);
                            }
                        }
                    }

                    if (frameTimingEnabled && !frameTimingPostPresentClosed) {
                        frameTimingPostPresentTicks = (Stopwatch.GetTimestamp() - frameTimingPostPresentStart);
                    }

                    // [frame-timing]: close out this iteration's interval (loop-top to here, right before the next
                    // loop-top re-check) and PUBLISH a sample that TILES it — the twelve phase buckets plus whatever is left
                    // over (principally this measurement's own overhead) —
                    // into the hub. Subscribers (the throttled stderr digest, a bench runner) read from there; the
                    // publish fires them synchronously on this thread.
                    if (frameTimingEnabled) {
                        var frameTimingGcPauseTicks = (GC.GetTotalPauseDuration().Ticks - frameTimingGcPauseStart);
                        var frameTimingGcCollections = (((GC.CollectionCount(generation: 0) + GC.CollectionCount(generation: 1)) + GC.CollectionCount(generation: 2)) - frameTimingGcCollectionsStart);
                        var frameTimingIntervalTicks = (Stopwatch.GetTimestamp() - frameTimingIterationStart);
                        var frameTimingRemainderTicks = ((((((((((((frameTimingIntervalTicks
                            - frameTimingPumpTicks)
                            - frameTimingClockTicks)
                            - frameTimingInputSnapshotTicks)
                            - frameTimingCommandApplyTicks)
                            - frameTimingSimulationStepTicks)
                            - frameTimingFixedStepOverheadTicks)
                            - frameTimingSimulationOutputTicks)
                            - frameTimingBeginFrameTicks)
                            - frameTimingProduceTicks)
                            - frameTimingPresentTicks)
                            - frameTimingPostPresentTicks)
                            - frameTimingPacerTicks);

                        static double ToMs(long ticks, long frequency) =>
                            (((double)ticks * 1000.0) / frequency);

                        m_frameTimingHub.Publish(sample: new FrameTimingSample(
                            ProducedFrameIndex: frameTimingProducedFrames,
                            IntervalMs: ToMs(frequency: frequency, ticks: frameTimingIntervalTicks),
                            PumpMs: ToMs(frequency: frequency, ticks: frameTimingPumpTicks),
                            ClockMs: ToMs(frequency: frequency, ticks: frameTimingClockTicks),
                            InputSnapshotMs: ToMs(frequency: frequency, ticks: frameTimingInputSnapshotTicks),
                            CommandApplyMs: ToMs(frequency: frequency, ticks: frameTimingCommandApplyTicks),
                            SimulationStepMs: ToMs(frequency: frequency, ticks: frameTimingSimulationStepTicks),
                            FixedStepOverheadMs: ToMs(frequency: frequency, ticks: frameTimingFixedStepOverheadTicks),
                            SimulationOutputMs: ToMs(frequency: frequency, ticks: frameTimingSimulationOutputTicks),
                            GpuDrainMs: ToMs(frequency: frequency, ticks: frameTimingBeginFrameTicks),
                            ProduceMs: ToMs(frequency: frequency, ticks: frameTimingProduceTicks),
                            PresentMs: ToMs(frequency: frequency, ticks: frameTimingPresentTicks),
                            PostPresentMs: ToMs(frequency: frequency, ticks: frameTimingPostPresentTicks),
                            PacerMs: ToMs(frequency: frequency, ticks: frameTimingPacerTicks),
                            RemainderMs: ToMs(frequency: frequency, ticks: frameTimingRemainderTicks),
                            GcPauseMs: ((double)frameTimingGcPauseTicks / TimeSpan.TicksPerMillisecond),
                            GcCollections: frameTimingGcCollections,
                            FixedSteps: frameTimingFixedSteps,
                            SkippedPresentTotal: (frameTimingSkipFeedback?.SkippedPresentCount ?? 0UL)
                        ));
                    }
                }

                m_frameTimingHub.Published -= PublishFrameTimingDigest;

                if (window.IsOpen) {
                    window.Close();
                    window.PollEvents();
                }

                m_logger.LogInformation("Native window closed; shutting the host down.");
            } finally {
                // Flush any buffered echo tail before teardown so the final lines a scripted run emits (e.g. right
                // before an --exit-after shutdown, or the frame a quit/exit verb lands) are never lost.
                m_bufferedOutput.Flush();

                // The loop's final Present submitted GPU work that the NEXT frame's BeginFrame would normally
                // wait on — but there is no next frame. Drain the device here so node/presenter teardown below
                // can't destroy resources still referenced by that last in-flight frame.
                if (m_rootHostContext.TryResolveCapability<IGpuDeviceContext>(capability: out var deviceContext)) {
                    deviceContext.WaitIdle();
                }

                m_root.Dispose();
                m_presenter.Dispose();
            }
        } finally {
            m_applicationLifetime.StopApplication();
        }
    }
    /// <summary>Recovers from a graphics device loss on the pump thread: the render tree releases its device-derived GPU
    /// resources (on the still-valid lost device), then the presenter rebuilds the device + presentation resources in
    /// place; the next frame rebuilds the node resources on the new device. Returns <see langword="false"/> (so the caller
    /// rethrows and the run ends) when the presenter cannot recover or recovery has failed too many times in a row.</summary>
    private bool TryRecoverFromDeviceLoss(NativeSurfaceBinding binding, DeviceLostException deviceLost, uint width, uint height, ref int streak) {
        ++streak;

        if (m_presenter is not IDeviceLostRecoverable recoverable) {
            m_logger.LogError(exception: deviceLost, message: "Graphics device lost (reason 0x{Reason:X}) but the active presenter cannot recover.", deviceLost.ReasonCode);

            return false;
        }

        if (streak > MaxConsecutiveDeviceLossRecoveries) {
            m_logger.LogError(exception: deviceLost, message: "Graphics device-loss recovery failed {Count} times in a row (reason 0x{Reason:X}); aborting the run.", MaxConsecutiveDeviceLossRecoveries, deviceLost.ReasonCode);

            return false;
        }

        m_logger.LogWarning(exception: deviceLost, message: "Graphics device lost (reason 0x{Reason:X}); recovering (attempt {Attempt}/{Max}).", deviceLost.ReasonCode, streak, MaxConsecutiveDeviceLossRecoveries);

        // Drain in-flight GPU work BEFORE any teardown. On a genuinely lost device this faults and is swallowed
        // (nothing will ever complete); on a still-healthy device — a recoverable RESET, or the synthetic test hook —
        // it is essential, because destroying command pools / image views still referenced by pending work is a
        // validation error and can crash the driver.
        if (m_rootHostContext.TryResolveCapability<IGpuDeviceContext>(capability: out var deviceContext)) {
            try {
                deviceContext.WaitIdle();
            } catch (DeviceLostException) {
                // Device already lost; there is no in-flight work to wait on.
            }
        }

        // Order matters: the node tree releases its GPU objects FIRST — they are children of the device and must go
        // before it does — then the presenter destroys + recreates the device IN PLACE (so the capability-published
        // context keeps its identity and nodes rebuild against the new handle next frame). Release once, here.
        m_root.OnDeviceLost();

        // Recreate the device, waiting out an extended device-ABSENT window: a real removal leaves no capable adapter
        // for seconds, and the fresh create keeps failing (surfaced by the backend as another DeviceLostException) until
        // it returns. Retry with backoff until the rebuild succeeds or the reacquire budget elapses.
        var reacquireDeadlineTimestamp = (Stopwatch.GetTimestamp() + (long)(DeviceReacquireBudgetSeconds * Stopwatch.Frequency));
        var waitedForDevice = false;

        while (true) {
            try {
                recoverable.RecoverFromDeviceLoss(binding: binding, height: height, width: width);

                if (waitedForDevice) {
                    m_logger.LogInformation(message: "A graphics device returned; presentation resources rebuilt.");
                }

                return true;
            } catch (DeviceLostException reacquireLoss) {
                if (Stopwatch.GetTimestamp() >= reacquireDeadlineTimestamp) {
                    // The device did not return within the budget. This also covers the case where it CANNOT return in
                    // this process: a full adapter removal (vs. a self-recovering driver reset) can leave the graphics
                    // driver unable to reinitialize in-process — the fresh device create keeps failing even after the
                    // adapter is back — and only a new process recovers. Either way, give up so the caller shuts down
                    // cleanly rather than hanging.
                    m_logger.LogError(exception: reacquireLoss, message: "The graphics device did not return within {Seconds}s of the loss (reason 0x{Reason:X}); it cannot be reinitialized in this process. Shutting down.", DeviceReacquireBudgetSeconds, reacquireLoss.ReasonCode);

                    return false;
                }

                if (!waitedForDevice) {
                    m_logger.LogWarning(message: "The graphics device is still absent; waiting up to {Seconds}s for it to return...", DeviceReacquireBudgetSeconds);

                    waitedForDevice = true;
                }

                Thread.Sleep(millisecondsTimeout: DeviceReacquireBackoffMilliseconds);
            }
        }
    }
    // GENLOCK election watch: when plural rhythm sources are registered with no election to break the tie, nothing
    // forwards to the pacer (the registry never picks an arbitrary winner) — announce it, with the ids, so the operator
    // can name one. The registry exposes the condition structurally (generation + contention flag), so this is a cheap
    // per-frame check that logs only when the election actually changes; the registry itself stays log-free.
    private void NoteExternalClockContention(ref int observedElectionGeneration) {
        var generation = m_externalClocks.ElectionGeneration;

        if (generation == observedElectionGeneration) {
            return;
        }

        observedElectionGeneration = generation;

        if (m_externalClocks.IsContended) {
            var sourceIds = m_externalClocks.SourceIds;

            m_logger.LogWarning(
                message: "Genlock: {Count} rhythm sources are registered ({SourceIds}) with no genlock election; the pacer free-runs until one is named.",
                sourceIds.Count,
                string.Join(separator: ", ", values: sourceIds)
            );
        }
    }
    // A clean frame rendered: if it follows one or more device-loss recoveries, announce that rendering is back and clear
    // the streak. (Without the announcement a recovery only logged "recovering…" then went quiet — reading as a failure
    // even though presents had resumed.)
    private void NoteFrameSucceeded(ref int streak) {
        if (streak > 0) {
            m_logger.LogInformation(message: "Graphics device recovered; rendering resumed after {Attempts} attempt(s).", streak);

            streak = 0;
        }
    }
    // Resolves the one-shot synthetic-device-loss injection time from LauncherOptions.SyntheticDeviceLossSeconds,
    // or null when the test hook is off. Render/test only.
    private static long? ResolveSyntheticDeviceLossTimestamp(double? seconds, long startTimestamp, long frequency) {
        return (((seconds is { } value) && (value > 0.0))
            ? (long?)(startTimestamp + (long)(value * frequency))
            : null);
    }
    // Throws a synthetic DeviceLostException once the configured time has elapsed (test hook only); flips the one-shot
    // flag so it fires exactly once.
    private static void ThrowIfSyntheticDeviceLossDue(long? at, ref bool fired) {
        if (
            (at is { } dueTimestamp) &&
            !fired &&
            (Stopwatch.GetTimestamp() >= dueTimestamp)
        ) {
            fired = true;

            throw new DeviceLostException(message: "Synthetic device-loss test injection (PUCK_TEST_DEVICE_LOSS).");
        }
    }
    private long ResolveRenderPeriod(DisplayTimingSnapshot displayTiming, long frequency, double requestedHertz) {
        var decision = PresentPacingPolicy.Resolve(timing: displayTiming, requestedHertz: requestedHertz);

        if (m_logger.IsEnabled(logLevel: LogLevel.Information)) {
            m_logger.LogInformation(
                "Display pacing: signal {Signal}; VRR {Support}, range {Range}, source {Source}; target {Target:0.###} Hz ({Basis}).",
                (displayTiming.Signal.IsKnown ? $"{displayTiming.Signal.Hertz:0.###} Hz" : "unknown"),
                displayTiming.VariableRefresh.Support,
                (displayTiming.VariableRefresh.Range is { } range ? $"{range.MinimumHertz:0.###}-{(range.MaximumHertz is { } maximum ? $"{maximum:0.###}" : "mode-max")} Hz" : "unknown"),
                displayTiming.VariableRefresh.Source,
                decision.TargetHertz,
                decision.Basis
            );
        }

        return decision.ToPeriodTicks(frequency: frequency);
    }
    // The [frame-timing] stderr digest, now ONE SUBSCRIBER of the frame-timing hub (the loop publishes every armed
    // iteration; a bench runner is another subscriber). One line per FrameTimingReportInterval newly PRODUCED frames
    // reports the slowest complete interval in that block and its literal bucket tiling. Reporting the block maximum,
    // rather than whichever frame happened to land on the modulo boundary, makes intermittent hitches attributable
    // without logging every frame and perturbing the cadence under investigation.
    private void PublishFrameTimingDigest(FrameTimingSample sample) {
        if (sample.ProducedFrameIndex <= m_frameTimingDigestLastProducedFrameIndex) {
            return;
        }

        m_frameTimingDigestLastProducedFrameIndex = sample.ProducedFrameIndex;
        ++m_frameTimingDigestSampleCount;

        if (sample.IntervalMs >= m_frameTimingDigestWorst.IntervalMs) {
            m_frameTimingDigestWorst = sample;
        }

        if (0UL != (m_frameTimingDigestSampleCount % FrameTimingReportInterval)) {
            return;
        }

        var worst = m_frameTimingDigestWorst;

        m_frameTimingDigestWorst = default;

        Console.Error.WriteLine(value: $"[frame-timing] worst-of-{FrameTimingReportInterval} frame {worst.ProducedFrameIndex} | interval {worst.IntervalMs:0.000}ms | pump {worst.PumpMs:0.000} | clock {worst.ClockMs:0.000} | input-snapshot {worst.InputSnapshotMs:0.000} | command-apply {worst.CommandApplyMs:0.000} | simulation-step {worst.SimulationStepMs:0.000} | fixed-overhead {worst.FixedStepOverheadMs:0.000} | sim-output {worst.SimulationOutputMs:0.000} | gpu-drain {worst.GpuDrainMs:0.000} | produce {worst.ProduceMs:0.000} | present {worst.PresentMs:0.000} | post-present {worst.PostPresentMs:0.000} | pacer {worst.PacerMs:0.000} | remainder {worst.RemainderMs:0.000} | gc-pause {worst.GcPauseMs:0.000} ({worst.GcCollections}) | steps {worst.FixedSteps} | skippedTotal {worst.SkippedPresentTotal}");
    }
    private static void WaitUntil(long deadlineTimestamp, long spinThreshold, long frequency, IPrecisionWaiter? precisionWaiter) {
        while (true) {
            var remaining = (deadlineTimestamp - Stopwatch.GetTimestamp());

            if (remaining <= 0L) {
                break;
            }

            if (remaining > spinThreshold) {
                // Wake within ~0.5 ms of (deadline - spinThreshold) via the high-resolution waiter, then spin the
                // remainder for an accurate edge; fall back to a coarse 1 ms sleep where no precision waiter exists.
                var sleepTicks = (remaining - spinThreshold);

                if ((precisionWaiter is null) || !precisionWaiter.TryWait(duration: TimeSpan.FromSeconds(value: ((double)sleepTicks / frequency)))) {
                    Thread.Sleep(millisecondsTimeout: 1);
                }
            } else {
                Thread.SpinWait(iterations: 48);
            }
        }
    }
}
