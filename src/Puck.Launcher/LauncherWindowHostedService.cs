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
    // A real device loss (driver crash/update, the adapter disabled/removed) leaves NO capable adapter for SECONDS: the
    // fresh device create keeps failing until it returns. Recovery waits out that window — retrying the rebuild with this
    // backoff for up to this budget — before giving up. These waits are ONE loss's recovery, so they do NOT advance the
    // consecutive-loss streak above (which guards against a device that drops again the instant it is recovered).
    private const int DeviceReacquireBackoffMilliseconds = 250;
    private const double DeviceReacquireBudgetSeconds = 10.0;
    // Cap on back-to-back device-loss recoveries with no successful frame between them, so a permanently-dead GPU (or a
    // presenter that cannot recover) fails loudly instead of spinning forever. Reset to 0 after any good frame.
    private const int MaxConsecutiveDeviceLossRecoveries = 8;

    private readonly IHostApplicationLifetime m_applicationLifetime;
    private readonly BufferedConsoleOutput m_bufferedOutput;
    private readonly FrameCaptureController? m_capture;
    private readonly ExternalClockRegistry m_externalClocks;
    private readonly IInputClock m_inputClock;
    private readonly StandardInputBacklog m_inputBacklog;
    private readonly InputRouter? m_inputRouter;
    private readonly ILogger<LauncherWindowHostedService> m_logger;
    private readonly LauncherOptions m_options;
    private readonly PresentPacingControl m_presentPacing;
    private readonly IPresentSurfaceReadback? m_presentReadback;
    private readonly ISurfacePresenter m_presenter;
    private readonly CommandRegistry m_registry;
    private readonly IRenderNode m_root;
    private readonly IHostContext m_rootHostContext;
    private readonly IFixedStepSimulation? m_simulation;
    private readonly ISnapshotInputCapture[] m_snapshotInputCaptures;
    private readonly TerminalControl m_terminal;
    private readonly TextCommandSource m_textSource;
    private readonly INativeWindowFactory m_windowFactory;

    public LauncherWindowHostedService(
        IHostApplicationLifetime applicationLifetime,
        BufferedConsoleOutput bufferedOutput,
        ExternalClockRegistry externalClocks,
        IInputClock inputClock,
        ILogger<LauncherWindowHostedService> logger,
        LauncherOptions options,
        PresentPacingControl presentPacing,
        ISurfacePresenter presenter,
        IRenderNode root,
        IHostContext rootHostContext,
        IEnumerable<InputRouter> inputRouters,
        IEnumerable<IFixedStepSimulation> simulations,
        IEnumerable<FrameCaptureController> captureControllers,
        IEnumerable<ISnapshotInputCapture> snapshotInputCaptures,
        CommandRegistry registry,
        TextCommandSource textSource,
        TerminalControl terminal,
        StandardInputBacklog inputBacklog,
        INativeWindowFactory windowFactory
    ) {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(bufferedOutput);
        ArgumentNullException.ThrowIfNull(captureControllers);
        ArgumentNullException.ThrowIfNull(externalClocks);
        ArgumentNullException.ThrowIfNull(inputClock);
        ArgumentNullException.ThrowIfNull(inputRouters);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(presentPacing);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootHostContext);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(textSource);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(inputBacklog);
        ArgumentNullException.ThrowIfNull(windowFactory);

        m_applicationLifetime = applicationLifetime;
        m_bufferedOutput = bufferedOutput;
        m_capture = LauncherHostLoop.SingleOrDefault(
            items: captureControllers,
            name: nameof(FrameCaptureController),
            hostDescription: "windowed host"
        );
        m_externalClocks = externalClocks;
        m_inputClock = inputClock;
        m_inputRouter = LauncherHostLoop.SingleOrDefault(
            items: inputRouters,
            name: nameof(InputRouter),
            hostDescription: "launcher"
        );
        m_logger = logger;
        m_options = options;
        m_presentPacing = presentPacing;
        m_presenter = presenter;
        m_presentReadback = (presenter as IPresentSurfaceReadback);
        m_root = root;
        m_rootHostContext = rootHostContext;
        m_registry = registry;
        m_textSource = textSource;
        m_simulation = LauncherHostLoop.SingleOrDefault(
            items: simulations,
            name: nameof(IFixedStepSimulation),
            hostDescription: "launcher"
        );
        m_snapshotInputCaptures = snapshotInputCaptures.ToArray();
        m_terminal = terminal;
        m_inputBacklog = inputBacklog;
        m_windowFactory = windowFactory;

        if ((m_simulation is null) != (m_inputRouter is null)) {
            throw new InvalidOperationException(message: "A fixed-step simulation and its InputRouter must be registered together. Use AddFixedStepSimulation<TSimulation>().");
        }

        // The console text door's OWN sink — bound to the Console principal when the router built it, so wiring it
        // here cannot choose what a submitted line acts as.
        m_registry.RouteSimulationTo(sink: m_inputRouter?.ConsoleTextSink);
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
                string.Join(
                    separator: ", ",
                    values: sourceIds
                )
            );
        }
    }
    // A clean frame rendered: if it follows one or more device-loss recoveries, announce that rendering is back and clear
    // the streak. (Without the announcement a recovery only logged "recovering…" then went quiet — reading as a failure
    // even though presents had resumed.)
    private void NoteFrameSucceeded(ref int streak) {
        if (streak > 0) {
            m_logger.LogInformation(
                message: "Graphics device recovered; rendering resumed after {Attempts} attempt(s).",
                streak
            );

            streak = 0;
        }
    }
    private long ResolveRenderPeriod(DisplayTimingSnapshot displayTiming, long frequency, double requestedHertz) {
        var decision = PresentPacingPolicy.Resolve(
            requestedHertz: requestedHertz,
            timing: displayTiming
        );

        if (m_logger.IsEnabled(logLevel: LogLevel.Information)) {
            m_logger.LogInformation(
                "Display pacing: signal {Signal}; VRR {Support}, range {Range}, source {Source}; target {Target:0.###} Hz ({Basis}).",
                (displayTiming.Signal.IsKnown
                ? $"{displayTiming.Signal.Hertz:0.###} Hz"
                : "unknown"),
                displayTiming.VariableRefresh.Support,
                ((displayTiming.VariableRefresh.Range is { } range)
                ? $"{range.MinimumHertz:0.###}-{((range.MaximumHertz is { } maximum)
                    ? $"{maximum:0.###}"
                    : "mode-max")} Hz"
                : "unknown"),
                displayTiming.VariableRefresh.Source,
                decision.TargetHertz,
                decision.Basis
            );
        }

        return decision.ToPeriodTicks(frequency: frequency);
    }
    // Resolves the one-shot synthetic-device-loss injection time from LauncherOptions.SyntheticDeviceLossSeconds,
    // or null when the test hook is off. Render/test only.
    private static long? ResolveSyntheticDeviceLossTimestamp(double? seconds, long startTimestamp, long frequency) {
        return (((seconds is { } value) && (value > 0.0))
            ? (long?)(startTimestamp + ((long)(value * frequency)))
            : null
        );
    }
    private void RunWindowLoop(CancellationToken stoppingToken) {
        try {
            using var window = m_windowFactory.Create();
            var activated = false;
            Exception? fault = null;

            try {
                if (window is not IWindowInputSource inputSource) {
                    throw new InvalidOperationException(message: "The launcher requires a window that can provide input.");
                }

                try {
                    m_presenter.Activate(
                        binding: window.CreateSurfaceBinding(),
                        height: window.Height,
                        width: window.Width
                    );
                } catch (Exception exception) {
                    throw new PresenterActivationException(innerException: exception);
                }

                activated = true;

                if (m_logger.IsEnabled(logLevel: LogLevel.Information)) {
                    m_logger.LogInformation(
                        "Opened native window \"{Title}\" ({Width}x{Height}); hosting the primary engine.",
                        window.Title,
                        window.Width,
                        window.Height
                    );
                }

                window.Show();

                var clock = TickClock.Start();
                // The shared fixed-step accumulator (Puck.Launcher.FixedStepPump) — null when no simulation is
                // registered (a composition root that drives no fixed-step sim at all), mirroring the ORIGINAL
                // m_simulation/m_inputRouter pairing check the constructor already enforces.
                var pump = FixedStepPump.CreateHosted(
                    holdsClock: false,
                    inputBacklog: m_inputBacklog,
                    inputClock: m_inputClock,
                    inputRouter: m_inputRouter,
                    output: m_bufferedOutput,
                    registry: m_registry,
                    simulation: m_simulation,
                    terminal: m_terminal,
                    textSource: m_textSource
                );
                var hostFrame = 0UL;
                var frequency = Stopwatch.Frequency;
                var maxFrameTicks = (EngineTicks.PerSecond / 4UL);
                // Display-aware presentation pacing. Active signal timing and explicit VRR capabilities are independent
                // facts; the host never turns selectable fixed modes into a fictional VRR range. Re-resolve only when the
                // window reports a display/topology change.
                var displayTimingInfo = (window as IDisplayTimingInfo);
                var displayTiming = (displayTimingInfo?.QueryDisplayTiming() ?? DisplayTimingSnapshot.Unknown);
                var displayConfigurationVersion = (displayTimingInfo?.DisplayConfigurationVersion ?? 0UL);
                const int DisplayTimingRetryLimit = 8;
                var displayTimingRetryAttemptsRemaining = (((displayTimingInfo is not null) && !displayTiming.IsKnown)
                    ? DisplayTimingRetryLimit
                    : 0
                );
                var nextDisplayTimingRetryTimestamp = 0L;
                var precisionWaiter = (window as IPrecisionWaiter);
                // An optional HELD root capability that sees every raw event. Mouse events can have two projections:
                // this observer updates presentation state, while WindowInputMapper independently feeds their
                // relative motion/buttons/wheel into command bindings. Resolved once: contributions never change.
                _ = m_rootHostContext.HoldsCapability<IWindowInputObserver>(capability: out var windowInputObserver);
                // Physical truth for edge-reported window controls. Each frame reasserts held keys and mouse buttons
                // in original press order, allowing a freshly-installed profile or modality to recover continuous
                // channels without synthesizing a Started edge.
                var heldDigitalInput = new HeldDigitalInputState();
                // Closed-loop present timing (VK_KHR_present_wait): the presenter confirms each present and reports the
                // instant it was confirmed. The pacer observes this rhythm — reporting the measured display interval
                // (delta between consecutive confirmed presents) — but does not re-anchor the render deadline to the
                // confirmation timestamp: for Vulkan that timestamp is the CPU instant vkWaitForPresentKHR returned
                // inside this frame's Present call (after produce ran), so anchoring to it would serialize produce and
                // the pacer wait, capping ~120 Hz runs near ~100 FPS. The deadline instead advances on an absolute slot
                // grid (see the pacer block below), letting produce + GPU work overlap the wait. Render-side only —
                // never touches the sim.
                var presentTiming = (m_presenter as IPresentTimingFeedback);
                var lastObservedPresentCount = 0u;
                var previousPresentTimestamp = 0L;
                var presentSampleCounter = 0;
                // Opt-in through LauncherOptions.LogPresentTiming: periodically log the
                // measured present interval — proof the closed loop is live and what the real display cadence is. Off
                // by default so a shipped run isn't noisy.
                var logPresentTiming = m_options.LogPresentTiming;
                // Genlock (latency phase-align): when an external frame producer (a live camera) publishes arrival
                // timestamps, the aligner biases the render deadline toward them with a light PI filter on the phase
                // error, so the frame that samples a fresh arrival starts (and presents) as soon after it as possible —
                // full VRR rate preserved, the fixed-step sim untouched. Silent with no publisher;
                // LauncherOptions.GenlockEnabled disables it.
                var genlock = new GenlockPhaseAligner(
                    clock: m_externalClocks.PacerClock,
                    enabled: m_options.GenlockEnabled,
                    logger: m_logger,
                    logPhase: logPresentTiming
                );
                // Starts behind any possible registry state so the first loop iteration always evaluates (and, when
                // sources registered before the loop, announces) the current election.
                var observedElectionGeneration = -1;
                // The live present-rate target the `present-rate` verb retargets: the pacer re-resolves its period when
                // this control's version advances (mirroring the display-change re-resolve below). Presentation only —
                // never reaches the fixed-step sim.
                var presentPacingVersion = m_presentPacing.Version;
                var renderPeriod = ResolveRenderPeriod(
                    displayTiming: displayTiming,
                    frequency: frequency,
                    requestedHertz: m_presentPacing.TargetHertz
                );
                var spinThreshold = LauncherHostLoop.SpinThreshold(frequency: frequency);
                var startTimestamp = Stopwatch.GetTimestamp();
                var nextRenderDeadline = startTimestamp;
                var exitAfterTimestamp = ((m_options.ExitAfter is { } exitAfter)
                    ? (startTimestamp + ((long)(exitAfter.TotalSeconds * frequency)))
                    : (long?)null
                );
                // Consecutive device-loss recoveries with no good frame in between; bounded so a permanently-dead GPU
                // (or a backend that can't recover) surfaces the failure instead of spinning forever.
                var deviceLossStreak = 0;
                // Test hook: a one-shot synthetic device loss N seconds in, to exercise recovery without real GPU churn.
                var syntheticDeviceLossAt = ResolveSyntheticDeviceLossTimestamp(
                    seconds: m_options.SyntheticDeviceLossSeconds,
                    startTimestamp: startTimestamp,
                    frequency: frequency
                );
                var syntheticDeviceLossFired = false;

                // The registered simulation declares its own rate; DefaultUpdateRate is the null-simulation fallback
                // (console pump alone) and the fallback while the registered simulation reports 0 (an authored
                // simulation.rateHz durable stop) — the pump's own calling cadence is presentation-adjacent host
                // pacing, never sim state, and EngineTicks.PerRate refuses zero outright. The registered simulation
                // still gates whether it actually steps internally (WorldSimulation.ShouldStepBoot); this value only
                // decides how often it is called, so a stopped world's window keeps rendering and its console keeps
                // answering.
                //
                // Resolved per iteration, not once before the loop: a live world.load can swap in a
                // differently-rated document mid-session, and the pump must adopt the new cadence the next iteration
                // rather than keep stepping at a stale rate.
                static ulong ResolveStepTicks(IFixedStepSimulation? simulation) {
                    var simRatePerSecond = (simulation?.RatePerSecond ?? LauncherHostLoop.DefaultUpdateRate);
                    var pumpRatePerSecond = ((simRatePerSecond == 0U)
                        ? LauncherHostLoop.DefaultUpdateRate
                        : simRatePerSecond
                    );

                    return EngineTicks.PerRate(ratePerSecond: pumpRatePerSecond);
                }

                while (
                    window.IsOpen &&
                    !stoppingToken.IsCancellationRequested
                ) {
                    // The number of whole fixed steps this iteration ran — feeds the frame context's DeltaTicks below.
                    var fixedSteps = 0UL;

                    window.PollEvents();

                    // PollEvents may have processed a display change. Immediately discard both old-monitor facts, then
                    // make bounded retries because Windows topology queries can be transiently unavailable mid-change.
                    if (
                        (displayTimingInfo is not null) &&
                        (displayTimingInfo.DisplayConfigurationVersion != displayConfigurationVersion)
                    ) {
                        displayConfigurationVersion = displayTimingInfo.DisplayConfigurationVersion;
                        displayTiming = DisplayTimingSnapshot.Unknown;
                        renderPeriod = ResolveRenderPeriod(
                            displayTiming: displayTiming,
                            frequency: frequency,
                            requestedHertz: m_presentPacing.TargetHertz
                        );
                        displayTimingRetryAttemptsRemaining = DisplayTimingRetryLimit;
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
                        renderPeriod = ResolveRenderPeriod(
                            displayTiming: displayTiming,
                            frequency: frequency,
                            requestedHertz: m_presentPacing.TargetHertz
                        );

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
                        renderPeriod = ResolveRenderPeriod(
                            displayTiming: displayTiming,
                            frequency: frequency,
                            requestedHertz: m_presentPacing.TargetHertz
                        );
                    }

                    // GENLOCK election watch: announce (once per election change) when plural rhythm sources are
                    // registered with no election, so the resulting silent free-run is visible to the operator.
                    NoteExternalClockContention(observedElectionGeneration: ref observedElectionGeneration);

                    while (inputSource.TryDequeueInput(inputEvent: out var windowInput)) {
                        var hasInputFocus = m_rootHostContext.HoldsCapability<IInputFocus>(capability: out var inputFocus);
                        var wasInputActive = (hasInputFocus && inputFocus.IsActiveFor(deviceId: windowInput.DeviceId));

                        // Hand the RAW event to the window input observer first, unconditionally (not focus-gated):
                        // it captures presentation/session-only state (pointer drag, a console's typed keystrokes)
                        // that never touches CaptureTick/CommandSnapshot below — the focus gate a few lines down is
                        // what stops a captured keystroke from ALSO driving the avatar or firing a bound command
                        // (see IWindowInputObserver's doc comment).
                        windowInputObserver?.Observe(inputEvent: in windowInput);

                        if (windowInput.Kind == WindowInputKind.FocusLost) {
                            // OS WINDOW focus loss (Alt-Tab, click-away) — distinct from the IInputFocus/TerminalControl
                            // check above, which is engine-terminal focus and never fires from an OS-level Alt-Tab.
                            // WindowInputMapper has no case for this kind; it must never reach it.
                            heldDigitalInput.Clear();
                            m_inputRouter?.ReleaseHeld();
                            continue;
                        }

                        if (windowInput.Kind == WindowInputKind.PointerPosition) {
                            // Absolute cursor coordinates remain presentation-only. The other mouse shapes below
                            // have a command projection in addition to the raw observer projection above.
                            continue;
                        }

                        // Stamp at the pump: the wndproc dispatched these during PollEvents above, so capture
                        // time ≈ now. Monotonic and sufficient to attribute the input to a fixed-step tick;
                        // per-event OS-event-time (GetMessageTime via OsTimeCorrelator) is a later refinement.
                        var signal = WindowInputMapper.ToInputSignal(inputEvent: in windowInput) with {
                            CaptureTick = m_inputClock.NowTicks,
                        };

                        heldDigitalInput.Observe(
                            frameKey: hostFrame,
                            signal: in signal
                        );
                        var isInputActive = (hasInputFocus && inputFocus.IsActiveFor(deviceId: signal.DeviceId));

                        if (
                            wasInputActive &&
                            isInputActive
                        ) {
                            // The router is the ONLY door physical input has. Its predecessor had a second, frame-driven
                            // branch for a root with no router — that path produced no tick, no recording, and no
                            // stamped principal, so it was deleted with the sources facet rather than secured. A root
                            // without an InputRouter is therefore a root with no bound input at all, BY DESIGN; the
                            // constructor above already refuses a simulation registered without one.
                            m_inputRouter?.Capture(signal: in signal);
                        } else {
                            // A released device still reaches the terminal plane containing commands such as
                            // `console`, which must be able to restore the very focus that ordinary bindings require.
                            // Requiring focus on BOTH sides of raw observation keeps a close gesture suppressed even
                            // though that observer restores focus, and suppresses the first event that associates a
                            // new text device with an already-open seat session. The router filters this path by the
                            // destination's declared input scope and resolves only the host-owned always-active plane.
                            m_inputRouter?.CaptureFocusExempt(signal: in signal);
                        }
                    }

                    // Reassert after the whole raw batch: a release in this frame has already removed its control,
                    // and every surviving control is emitted in physical first-down order. Re-resolve focus now so
                    // a menu/terminal close in the event batch can make held channels live immediately.
                    if (heldDigitalInput.Count > 0) {
                        var heldCaptureTick = m_inputClock.NowTicks;
                        var heldHasInputFocus = m_rootHostContext.HoldsCapability<IInputFocus>(capability: out var heldInputFocus);

                        for (var heldIndex = 0; (heldIndex < heldDigitalInput.Count); heldIndex++) {
                            if (!heldDigitalInput.TryReassert(
                                captureTick: heldCaptureTick,
                                frameKey: hostFrame,
                                index: heldIndex,
                                signal: out var heldSignal
                            )) {
                                continue;
                            }

                            if (
                                heldHasInputFocus &&
                                heldInputFocus.IsActiveFor(deviceId: heldSignal.DeviceId)
                            ) {
                                m_inputRouter?.Capture(signal: in heldSignal);
                            } else {
                                m_inputRouter?.CaptureFocusExempt(signal: in heldSignal);
                            }
                        }
                    }

                    for (var captureIndex = 0; (captureIndex < m_snapshotInputCaptures.Length); captureIndex++) {
                        m_snapshotInputCaptures[captureIndex].CaptureFrame(frameKey: hostFrame);
                    }

                    hostFrame++;

                    m_textSource.Collect();

                    // Flush the command pump's buffered result echoes ONCE, right after the drain that produced them
                    // (see BufferedConsoleOutput): every line submitted this frame appended to the buffer during Collect,
                    // so one flush here emits the whole burst in a single write, preserving FIFO order. The teardown
                    // finally-block flushes again so the final lines before an --exit-after shutdown are never lost.
                    m_bufferedOutput.Flush();

                    if (
                        (exitAfterTimestamp is { } deadline) &&
                        (Stopwatch.GetTimestamp() >= deadline)
                    ) {
                        m_terminal.RequestExit();
                    }

                    var deltaTicks = clock.Sample();
                    // Re-resolved every iteration — see ResolveStepTicks' own remarks above.
                    var stepTicks = ResolveStepTicks(simulation: m_simulation);

                    if (pump is { } activePump) {
                        fixedSteps += ((ulong)activePump.Advance(
                            deltaTicks: deltaTicks,
                            maxFrameTicks: maxFrameTicks,
                            stepTicks: stepTicks
                        ));
                    }

                    // Simulation-routed console handlers run while snapshots are applied above. Flush their real
                    // results in this iteration rather than leaving them buffered until the next rendered frame.
                    m_bufferedOutput.Flush();

                    var width = window.Width;
                    var height = window.Height;

                    // The frame body (present-side GPU work) can surface a device-lost error (DXGI_ERROR_DEVICE_REMOVED /
                    // VK_ERROR_DEVICE_LOST) at BeginFrame's wait-for-idle, the node tree's own submit, or Present, all
                    // translated to a neutral DeviceLostException at the backend boundary. Catch it here, recover the
                    // device + resources, and resume. The fixed-step sim above is already advanced for this tick and is
                    // not touched — a recovery that burns several wall-clock frames is absorbed by the maxFrameTicks
                    // clamp, so a recorded run produces identical sim ticks regardless of recovery hitches.
                    try {
                        // Test hook (PUCK_TEST_DEVICE_LOSS=<seconds>): inject ONE synthetic device loss to exercise the
                        // full recovery path (catch -> node reset -> device recreate -> resume) on a HEALTHY GPU — no
                        // driver reset, no black-screen risk. Validates the rebuild machinery; the real native-detection
                        // path is exercised separately by a true loss (e.g. Win+Ctrl+Shift+B).
                        ThrowIfSyntheticDeviceLossDue(
                            at: syntheticDeviceLossAt,
                            fired: ref syntheticDeviceLossFired
                        );

                        // BeginFrame recreates presentation resources when the size changed and waits for the
                        // previous frame's GPU work, so the node tree can safely reuse its per-frame resources.
                        m_presenter.BeginFrame(
                            height: height,
                            width: width
                        );

                        if (
                            (width > 0) &&
                            (height > 0)
                        ) {
                            var frameContext = new FrameContext(
                                AccumulatorTicks: (pump?.AccumulatorTicks ?? 0UL),
                                DeltaTicks: (fixedSteps * stepTicks),
                                ElapsedTicks: (pump?.ElapsedTicks ?? 0UL),
                                FrameDeltaTicks: deltaTicks,
                                Host: m_rootHostContext,
                                StepTicks: stepTicks,
                                TargetHeight: height,
                                TargetWidth: width
                            );
                            var surface = m_root.ProduceFrame(context: in frameContext);

                            m_capture?.Capture(
                                context: in frameContext,
                                readback: m_presentReadback,
                                surface: surface
                            );

                            m_presenter.Present(surface: surface);
                        }

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

                    if (m_terminal.TryConsumeExit()) {
                        window.Close();
                        window.PollEvents();
                        break;
                    }

                    if (renderPeriod > 0L) {
                        // Grid-anchored pacing. The render deadline advances by exactly renderPeriod from the previous
                        // deadline — an absolute present-slot grid — not from when produce/present finished this frame.
                        // That lets this frame's produce + GPU work overlap the wait for the next slot: the wait is
                        // only the slack between the fixed grid point and however long produce ran, so the
                        // loop-to-loop interval is the slot itself (renderPeriod), not produce + slot.
                        //
                        // The closed loop stays observed, not authoritative: when the presenter confirms a new
                        // present, the measured display interval (delta between confirmed presents) is reported, but
                        // the confirmation timestamp does not move the deadline (see the presentTiming remarks
                        // above). When no present-timing capability is present this whole block is a no-op.
                        if (presentTiming is not null) {
                            var sample = presentTiming.LastPresentTiming;

                            if (
                                sample.IsAvailable &&
                                (sample.PresentCount != lastObservedPresentCount)
                            ) {
                                lastObservedPresentCount = sample.PresentCount;

                                // Diagnostic: the measured display-present interval between confirmed presents — throttled
                                // so it isn't noisy, and only when opted in. This is the at-a-glance "closed loop is live".
                                if (
                                    logPresentTiming &&
                                    (previousPresentTimestamp > 0L) &&
                                    (0 == (++presentSampleCounter % 120)) &&
                                    m_logger.IsEnabled(logLevel: LogLevel.Information)
                                ) {
                                    var intervalMilliseconds = ((((double)(sample.PresentTimestampTicks - previousPresentTimestamp)) * 1000.0) / frequency);

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
                        nextRenderDeadline = genlock.Apply(
                            deadline: (nextRenderDeadline + renderPeriod),
                            frequency: frequency,
                            renderPeriod: renderPeriod
                        );

                        var nowTimestamp = Stopwatch.GetTimestamp();

                        // CATCH-UP: a frame that overran its slot (GPU-bound, or a one-off hitch) is already more than a
                        // full slot past the next grid point. Re-origin the grid at now — jump to the next slot — instead
                        // of accumulating the missed slots as debt (which would burst several presents back-to-back to
                        // "catch up"). A GPU-bound frame therefore never waits: its interval is its own GPU time, unchanged.
                        if ((nowTimestamp - nextRenderDeadline) > renderPeriod) {
                            nextRenderDeadline = nowTimestamp;
                        } else {
                            LauncherHostLoop.WaitUntil(
                                deadlineTimestamp: nextRenderDeadline,
                                frequency: frequency,
                                precisionWaiter: precisionWaiter,
                                spinThreshold: spinThreshold
                            );
                        }
                    }
                }

                if (window.IsOpen) {
                    window.Close();
                    window.PollEvents();
                }

                m_logger.LogInformation("Native window closed; shutting the host down.");
            } catch (Exception exception) {
                fault = exception;

                throw;
            } finally {
                LauncherHostRun.RunTeardown(
                    fault,
                    m_logger,
                    // Flush any buffered echo tail before teardown so the final lines a scripted run emits (e.g. right
                    // before an --exit-after shutdown, or the frame a quit/exit verb lands) are never lost.
                    ("flush output", m_bufferedOutput.Flush),
                    // Before the render root goes: a capture still owed a frame is decided while the chain that would
                    // have served it is alive, never refused by the disposal of a node still holding it.
                    ("settle owed frames", () => m_simulation?.SettleOwedFrames()),
                    // The loop's final Present submitted GPU work that the NEXT frame's BeginFrame would normally
                    // wait on — but there is no next frame. Drain the device here so node/presenter teardown below
                    // can't destroy resources still referenced by that last in-flight frame. A presenter that never
                    // activated submitted nothing, so there is nothing to drain.
                    ("drain device", () => {
                        if (
                            activated &&
                            m_rootHostContext.TryResolveCapability<IGpuDeviceContext>(capability: out var deviceContext)
                        ) {
                            deviceContext.WaitIdle();
                        }
                    }
                ),
                    ("dispose render root", m_root.Dispose),
                    ("dispose presenter", m_presenter.Dispose)
                );
            }
        } finally {
            m_applicationLifetime.StopApplication();
        }
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
    /// <summary>Recovers from a graphics device loss on the pump thread: the render tree releases its device-derived GPU
    /// resources (on the still-valid lost device), then the presenter rebuilds the device + presentation resources in
    /// place; the next frame rebuilds the node resources on the new device. Returns <see langword="false"/> (so the caller
    /// rethrows and the run ends) when the presenter cannot recover or recovery has failed too many times in a row.</summary>
    private bool TryRecoverFromDeviceLoss(NativeSurfaceBinding binding, DeviceLostException deviceLost, uint width, uint height, ref int streak) {
        ++streak;

        if (m_presenter is not IDeviceLostRecoverable recoverable) {
            m_logger.LogError(
                exception: deviceLost,
                message: "Graphics device lost (reason 0x{Reason:X}) but the active presenter cannot recover.",
                deviceLost.ReasonCode
            );

            return false;
        }

        if (streak > MaxConsecutiveDeviceLossRecoveries) {
            m_logger.LogError(
                exception: deviceLost,
                message: "Graphics device-loss recovery failed {Count} times in a row (reason 0x{Reason:X}); aborting the run.",
                MaxConsecutiveDeviceLossRecoveries,
                deviceLost.ReasonCode
            );

            return false;
        }

        m_logger.LogWarning(
            exception: deviceLost,
            message: "Graphics device lost (reason 0x{Reason:X}); recovering (attempt {Attempt}/{Max}).",
            deviceLost.ReasonCode,
            streak,
            MaxConsecutiveDeviceLossRecoveries
        );

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
        var reacquireDeadlineTimestamp = (Stopwatch.GetTimestamp() + ((long)(DeviceReacquireBudgetSeconds * Stopwatch.Frequency)));
        var waitedForDevice = false;

        while (true) {
            try {
                recoverable.RecoverFromDeviceLoss(
                    binding: binding,
                    height: height,
                    width: width
                );

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
                    m_logger.LogError(
                        exception: reacquireLoss,
                        message: "The graphics device did not return within {Seconds}s of the loss (reason 0x{Reason:X}); it cannot be reinitialized in this process. Shutting down.",
                        DeviceReacquireBudgetSeconds,
                        reacquireLoss.ReasonCode
                    );

                    return false;
                }

                if (!waitedForDevice) {
                    m_logger.LogWarning(
                        message: "The graphics device is still absent; waiting up to {Seconds}s for it to return...",
                        DeviceReacquireBudgetSeconds
                    );

                    waitedForDevice = true;
                }

                Thread.Sleep(millisecondsTimeout: DeviceReacquireBackoffMilliseconds);
            }
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => LauncherHostLoop.RunPump(
        name: "Puck.Launcher Window Pump",
        pump: () => RunWindowLoop(stoppingToken: stoppingToken)
    );
}
