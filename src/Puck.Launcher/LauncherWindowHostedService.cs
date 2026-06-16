using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher.Vulkan;
using Puck.Platform;

namespace Puck.Launcher;

/// <summary>
/// The outermost host loop — the dumb terminal. It owns the window + swapchain, and each frame drives the
/// single primary <see cref="IRenderNode"/> to produce one surface, then blits that surface to the
/// swapchain. The terminal knows nothing about the world — only the one surface the engine hands up. The
/// engine drives the terminal's lifecycle through the baton it was handed on the root host context; this
/// loop merely drains the resulting exit request (and honors <c>--exit-after</c> for scripted runs).
/// </summary>
internal sealed class LauncherWindowHostedService : BackgroundService {
    // Below this much time remaining the pacer busy-waits for an accurate wake-up; above it, it sleeps.
    private const int SpinThresholdMilliseconds = 2;
    // Render pacing cap (Hz); a divisor of EngineTicks.PerSecond so the period is a whole number of ticks.
    private const uint TargetRenderRate = 60U;
    // Fixed simulation rate (Hz); a divisor of EngineTicks.PerSecond so the step is a whole number of ticks.
    private const uint TargetUpdateRate = 240U;

    private readonly IHostApplicationLifetime m_applicationLifetime;
    private readonly SurfaceCompositor m_compositor;
    private readonly ILogger<LauncherWindowHostedService> m_logger;
    private readonly LauncherOptions m_options;
    private readonly VulkanRenderer m_renderer;
    private readonly IRenderNode m_root;
    private readonly IHostContext m_rootHostContext;
    private readonly CommandShell m_shell;
    private readonly TerminalControl m_terminal;
    private readonly INativeWindowFactory m_windowFactory;

    public LauncherWindowHostedService(
        IHostApplicationLifetime applicationLifetime,
        SurfaceCompositor compositor,
        ILogger<LauncherWindowHostedService> logger,
        LauncherOptions options,
        VulkanRenderer renderer,
        IRenderNode root,
        IHostContext rootHostContext,
        CommandShell shell,
        TerminalControl terminal,
        INativeWindowFactory windowFactory
    ) {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootHostContext);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(windowFactory);

        m_applicationLifetime = applicationLifetime;
        m_compositor = compositor;
        m_logger = logger;
        m_options = options;
        m_renderer = renderer;
        m_root = root;
        m_rootHostContext = rootHostContext;
        m_shell = shell;
        m_terminal = terminal;
        m_windowFactory = windowFactory;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) {
        // Native windows and Vulkan presentation are single-thread affine: both the message pump and the
        // renderer must live on the thread that created the window, and that work must not block host
        // startup. So the loop owns a dedicated thread and bridges back via IHostApplicationLifetime.
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
                m_renderer.Initialize(window: window);
                m_compositor.Initialize();

                m_logger.LogInformation(
                    "Opened native window \"{Title}\" ({Width}x{Height}); hosting the primary SDF engine.",
                    window.Title,
                    window.Width,
                    window.Height
                );
                window.Show();

                var clock = TickClock.Start();
                var stepTicks = EngineTicks.PerRate(ratePerSecond: TargetUpdateRate);
                // Cap a single frame's advance to a quarter second so a long stall (debugger break, GC pause,
                // the machine sleeping) can't trigger an unbounded simulation catch-up — the "spiral of death".
                var maxFrameTicks = (EngineTicks.PerSecond / 4UL);

                var frequency = Stopwatch.Frequency;
                var renderPeriod = (frequency / TargetRenderRate);
                var spinThreshold = ((frequency / 1000L) * SpinThresholdMilliseconds);
                var startTimestamp = Stopwatch.GetTimestamp();
                var nextRenderDeadline = startTimestamp;
                var exitAfterTimestamp = ((m_options.ExitAfterSeconds is { } exitAfterSeconds)
                    ? (startTimestamp + (long)(exitAfterSeconds * frequency))
                    : (long?)null);

                var accumulatorTicks = 0UL;
                var elapsedTicks = 0UL;

                while (
                    window.IsOpen &&
                    !stoppingToken.IsCancellationRequested
                ) {
                    window.PollEvents();

                    m_shell.BeginFrame();

                    // Route the window's input only to the engine that holds input focus (the held
                    // capability). With a single primary engine it always does; a hosted child granted focus
                    // would receive input here instead, and an engine that has released focus receives none.
                    if (
                        m_rootHostContext.HoldsCapability<IInputFocus>(capability: out var inputFocus) &&
                        inputFocus.IsActive
                    ) {
                        while (window.TryDequeueInput(inputEvent: out var inputEvent)) {
                            m_shell.Enqueue(packet: inputEvent);
                        }
                    }

                    m_shell.Collect();

                    // The scripted-run deadline drives the terminal through the same baton the engine uses, so
                    // a headless run exits down the identical path as a typed `quit`.
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

                    // Drain the accumulator in whole fixed steps: the simulation only ever advances by an exact
                    // multiple of the fixed step, so timing is deterministic and never strobes. The sub-step
                    // remainder stays in the accumulator and is handed to render as the interpolation alpha.
                    var consumedTicks = ((accumulatorTicks / stepTicks) * stepTicks);

                    accumulatorTicks -= consumedTicks;
                    elapsedTicks += consumedTicks;

                    // BeginFrame may (re)create presentation resources, which rebuilds the compositor's blit
                    // pipeline via PresentationResourcesRecreated.
                    m_renderer.BeginFrame();

                    var width = m_renderer.ViewportWidth;
                    var height = m_renderer.ViewportHeight;

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

                        m_compositor.Blit(surface: surface);
                    }

                    if (m_terminal.TryConsumeExit()) {
                        window.Close();
                        window.PollEvents();
                        break;
                    }

                    // Pace the render to the target cadence with a sleep-then-spin hybrid: sleep while there is
                    // time to spare (which yields the core), then busy-wait the final sub-millisecond for an
                    // accurate wake-up. Presentation is Mailbox/Immediate (non-blocking), so without this the
                    // loop would peg a core at 100%.
                    nextRenderDeadline += renderPeriod;

                    var nowTimestamp = Stopwatch.GetTimestamp();

                    if ((nowTimestamp - nextRenderDeadline) > renderPeriod) {
                        // Fell more than a full period behind (a stall): resync the cadence to now rather than
                        // bursting a run of catch-up frames to "make up" the lost time.
                        nextRenderDeadline = nowTimestamp;
                    } else {
                        PaceUntil(
                            deadlineTimestamp: nextRenderDeadline,
                            spinThreshold: spinThreshold
                        );
                    }
                }

                if (window.IsOpen) {
                    window.Close();
                    window.PollEvents();
                }

                m_logger.LogInformation("Native window closed; shutting the host down.");
            } finally {
                // Tear down the engine (and its renderer) and the compositor — each waits for device idle and
                // frees its GPU resources — BEFORE the renderer that owns the device.
                m_root.Dispose();
                m_compositor.Dispose();
                m_renderer.Dispose();
            }
        } finally {
            m_applicationLifetime.StopApplication();
        }
    }

    // Waits until the deadline (a Stopwatch timestamp), sleeping while there is comfortable slack and
    // busy-spinning the final stretch so the wake-up lands close to the deadline. Thread.Sleep granularity
    // depends on the OS timer, so the spin tail — not the sleep — is what makes the cadence accurate.
    private static void PaceUntil(long deadlineTimestamp, long spinThreshold) {
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
