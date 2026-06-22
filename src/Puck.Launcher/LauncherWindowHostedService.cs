using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions;
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
    // Cap the present rate this far below the display's maximum refresh so the cadence stays inside the variable-refresh
    // (VRR) window rather than pinning at the top edge, where many displays drop VRR back to fixed vsync.
    private const double VrrCapHeadroomHertz = 3.0;

    private readonly IHostApplicationLifetime m_applicationLifetime;
    private readonly IInputClock m_inputClock;
    private readonly ILogger<LauncherWindowHostedService> m_logger;
    private readonly LauncherOptions m_options;
    private readonly ISurfacePresenter m_presenter;
    private readonly IRenderNode m_root;
    private readonly IHostContext m_rootHostContext;
    private readonly CommandShell m_shell;
    private readonly TerminalControl m_terminal;
    private readonly INativeWindowFactory m_windowFactory;

    public LauncherWindowHostedService(
        IHostApplicationLifetime applicationLifetime,
        IInputClock inputClock,
        ILogger<LauncherWindowHostedService> logger,
        LauncherOptions options,
        ISurfacePresenter presenter,
        IRenderNode root,
        IHostContext rootHostContext,
        CommandShell shell,
        TerminalControl terminal,
        INativeWindowFactory windowFactory
    ) {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(inputClock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootHostContext);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(windowFactory);

        m_applicationLifetime = applicationLifetime;
        m_inputClock = inputClock;
        m_logger = logger;
        m_options = options;
        m_presenter = presenter;
        m_root = root;
        m_rootHostContext = rootHostContext;
        m_shell = shell;
        m_terminal = terminal;
        m_windowFactory = windowFactory;
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
                if (window is not INativeSurfaceSourceProvider surfaceSource) {
                    throw new InvalidOperationException(message: "The launcher requires a window that can provide a native surface binding.");
                }

                if (window is not IWindowInputSource inputSource) {
                    throw new InvalidOperationException(message: "The launcher requires a window that can provide input.");
                }

                m_presenter.Activate(
                    binding: surfaceSource.CreateSurfaceBinding(),
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
                var elapsedTicks = 0UL;
                var frequency = Stopwatch.Frequency;
                var maxFrameTicks = (EngineTicks.PerSecond / 4UL);
                // VRR pacing: clamp the render cadence into the display's refresh window (presentation only — the fixed
                // sim step below is untouched). The window reports its range; the high-resolution waiter (when present)
                // makes the pacer hit the cadence accurately. Both are resolved once here.
                var refreshRange = ((window is IDisplayRefreshInfo refreshInfo) ? refreshInfo.QueryRefreshRange() : DisplayRefreshRange.Unknown);
                var precisionWaiter = (window as IPrecisionWaiter);
                // CLOSED-LOOP present timing: when the presenter reports display-confirmed present times, phase-lock the
                // deadline to them instead of free-accumulating the period (which drifts from the real vblank). Absent it,
                // the pacer stays open-loop. Render-side only — never touches the fixed-step sim.
                var presentTiming = (m_presenter as IPresentTimingFeedback);
                var lastObservedPresentCount = 0u;
                var presentTimingPrimed = false;
                var previousPresentTimestamp = 0L;
                var presentSampleCounter = 0;
                // Opt-in (PUCK_PRESENT_TIMING=1): periodically log the measured present interval — proof the closed loop
                // is live and what the real display cadence is. Off by default so a shipped run isn't noisy.
                var logPresentTiming = string.Equals(Environment.GetEnvironmentVariable(variable: "PUCK_PRESENT_TIMING"), "1", comparisonType: StringComparison.Ordinal);
                var renderPeriod = ResolveRenderPeriod(refreshRange: refreshRange, frequency: frequency);
                var spinThreshold = ((frequency / 1000L) * SpinThresholdMilliseconds);
                var stepTicks = EngineTicks.PerRate(ratePerSecond: TargetUpdateRate);
                var startTimestamp = Stopwatch.GetTimestamp();
                var nextRenderDeadline = startTimestamp;
                var exitAfterTimestamp = ((m_options.ExitAfter is { } exitAfter)
                    ? (startTimestamp + (long)(exitAfter.TotalSeconds * frequency))
                    : (long?)null);

                while (
                    window.IsOpen &&
                    !stoppingToken.IsCancellationRequested
                ) {
                    window.PollEvents();

                    // When the window is not focused, key/button releases are not delivered, so drop any held
                    // inputs — otherwise a value down at the moment focus was lost would stay stuck.
                    if (
                        m_rootHostContext.HoldsCapability<IInputFocus>(capability: out var heldFocus) &&
                        !heldFocus.IsActiveFor(deviceId: default)
                    ) {
                        m_shell.ReleaseHeld();
                    }

                    m_shell.BeginFrame();

                    while (inputSource.TryDequeueInput(inputEvent: out var windowInput)) {
                        // Stamp at the pump: the wndproc dispatched these during PollEvents above, so capture
                        // time ≈ now. Monotonic and sufficient to attribute the input to a fixed-step tick;
                        // per-event OS-event-time (GetMessageTime via OsTimeCorrelator) is a later refinement.
                        var signal = WindowInputMapper.ToInputSignal(inputEvent: in windowInput) with {
                            CaptureTick = m_inputClock.NowTicks,
                        };

                        if (
                            m_rootHostContext.HoldsCapability<IInputFocus>(capability: out var inputFocus) &&
                            inputFocus.IsActiveFor(signal.DeviceId)
                        ) {
                            m_shell.Enqueue(signal: signal);
                        }
                    }

                    m_shell.Collect();

                    if (
                        (exitAfterTimestamp is { } deadline) &&
                        (Stopwatch.GetTimestamp() >= deadline)
                    ) {
                        m_terminal.RequestExit();
                    }

                    var deltaTicks = clock.Sample();

                    if (deltaTicks > maxFrameTicks) {
                        deltaTicks = maxFrameTicks;
                    }

                    accumulatorTicks += deltaTicks;

                    var consumedTicks = ((accumulatorTicks / stepTicks) * stepTicks);

                    accumulatorTicks -= consumedTicks;
                    elapsedTicks += consumedTicks;

                    var width = window.Width;
                    var height = window.Height;

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
                            AccumulatorTicks: accumulatorTicks,
                            DeltaTicks: consumedTicks,
                            ElapsedTicks: elapsedTicks,
                            Host: m_rootHostContext,
                            StepTicks: stepTicks,
                            TargetHeight: height,
                            TargetWidth: width
                        );
                        var surface = m_root.ProduceFrame(context: in frameContext);

                        m_presenter.Present(surface: surface);
                    }

                    if (m_terminal.TryConsumeExit()) {
                        window.Close();
                        window.PollEvents();
                        break;
                    }

                    if (renderPeriod > 0L) {
                        // Closed loop: when the presenter confirms a NEW present, re-anchor the deadline to the display's
                        // actual present timestamp (QPC == Stopwatch ticks) so the cadence phase-locks to vblank rather
                        // than drifting on the computed period. Guarded against a stale/absurd sample (the catch-up clamp
                        // below also absorbs it). When no present-timing capability is present, this is a no-op and the
                        // pacer stays open-loop.
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

                                if (
                                    presentTimingPrimed &&
                                    (Math.Abs(value: (Stopwatch.GetTimestamp() - sample.PresentTimestampTicks)) < (renderPeriod * 4L))
                                ) {
                                    nextRenderDeadline = sample.PresentTimestampTicks;
                                }

                                presentTimingPrimed = true;
                            }
                        }

                        nextRenderDeadline += renderPeriod;

                        var nowTimestamp = Stopwatch.GetTimestamp();

                        if ((nowTimestamp - nextRenderDeadline) > renderPeriod) {
                            nextRenderDeadline = nowTimestamp;
                        } else {
                            WaitUntil(
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
            } finally {
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
    // Resolves the per-frame present period (in Stopwatch ticks) the pacer holds to. With a known display range it
    // clamps into the VRR window — uncapped runs just below the maximum, a configured rate is clamped into the window;
    // with no display information it keeps the legacy behavior (fixed configured rate, or 0 = free-run). PRESENTATION
    // ONLY — the variable rate never reaches the fixed-step sim, so determinism is unaffected.
    private long ResolveRenderPeriod(DisplayRefreshRange refreshRange, long frequency) {
        var configuredHertz = ((m_options.TargetRenderRate is { } rate and > 0) ? (double?)rate : null);

        if (!refreshRange.IsKnown) {
            return ((configuredHertz is { } legacyHertz) ? (long)(frequency / legacyHertz) : 0L);
        }

        var capHertz = Math.Max(refreshRange.MinimumHertz, (refreshRange.MaximumHertz - VrrCapHeadroomHertz));
        var targetHertz = Math.Clamp(value: (configuredHertz ?? capHertz), max: capHertz, min: refreshRange.MinimumHertz);

        if (m_logger.IsEnabled(logLevel: LogLevel.Information)) {
            m_logger.LogInformation(
                "VRR pacing: display [{Min:0.#}-{Max:0.#}] Hz (current {Current:0.#}); present clamped to {Target:0.#} Hz.",
                refreshRange.MinimumHertz,
                refreshRange.MaximumHertz,
                refreshRange.CurrentHertz,
                targetHertz
            );
        }

        // Guard the divide: a (contract-legal but degenerate) range with a non-positive clamp target falls back to
        // free-run rather than producing an Infinity/garbage period.
        return ((targetHertz > 0.0) ? (long)(frequency / targetHertz) : 0L);
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

                if ((precisionWaiter is null) || !precisionWaiter.Wait(dueTime: TimeSpan.FromSeconds((double)sleepTicks / frequency))) {
                    Thread.Sleep(millisecondsTimeout: 1);
                }
            } else {
                Thread.SpinWait(iterations: 48);
            }
        }
    }
}
