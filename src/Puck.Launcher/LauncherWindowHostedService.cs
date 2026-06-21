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

    private readonly IHostApplicationLifetime m_applicationLifetime;
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
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootHostContext);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(windowFactory);

        m_applicationLifetime = applicationLifetime;
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
                var renderPeriod = ((m_options.TargetRenderRate is { } rate and > 0)
                    ? (frequency / rate)
                    : 0L);
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
                        var signal = WindowInputMapper.ToInputSignal(inputEvent: in windowInput);

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
                        nextRenderDeadline += renderPeriod;

                        var nowTimestamp = Stopwatch.GetTimestamp();

                        if ((nowTimestamp - nextRenderDeadline) > renderPeriod) {
                            nextRenderDeadline = nowTimestamp;
                        } else {
                            WaitUntil(
                                deadlineTimestamp: nextRenderDeadline,
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
                m_root.Dispose();
                m_presenter.Dispose();
            }
        } finally {
            m_applicationLifetime.StopApplication();
        }
    }
    private static void WaitUntil(long deadlineTimestamp, long spinThreshold) {
        while (true) {
            var remaining = (deadlineTimestamp - Stopwatch.GetTimestamp());

            if (remaining <= 0L) {
                break;
            }

            if (remaining > spinThreshold) {
                Thread.Sleep(millisecondsTimeout: 1);
            } else {
                Thread.SpinWait(iterations: 48);
            }
        }
    }
}
