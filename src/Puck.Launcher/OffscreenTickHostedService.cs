using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Pacing;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>
/// The offscreen boot shape's outermost host loop — a real GPU device and the composed-frame render pipeline with NO
/// window and NO swapchain. Paces the SAME <see cref="FixedStepPump"/> <see cref="HeadlessTickHostedService"/> drives
/// (wall clock converts to engine ticks exactly, never enters simulation state), and — because there is no present
/// cadence to ride — produces one composed frame per host-loop iteration, right after the fixed-step pump advances:
/// frame pacing rides the fixed-step pump's own cadence instead of vsync. The console pump and every registered
/// <see cref="ISnapshotInputCapture"/> contribution run every iteration exactly like the other two host loops.
/// </summary>
public sealed class OffscreenTickHostedService : BackgroundService {
    private readonly IHostApplicationLifetime m_applicationLifetime;
    private readonly BufferedConsoleOutput m_bufferedOutput;
    private readonly IInputClock m_inputClock;
    private readonly InputRouter? m_inputRouter;
    private readonly ILogger<OffscreenTickHostedService> m_logger;
    private readonly LauncherOptions m_options;
    private readonly IPrecisionWaiter? m_precisionWaiter;
    private readonly OffscreenRenderOptions m_renderOptions;
    private readonly IRenderNode m_root;
    private readonly IHostContext m_rootHostContext;
    private readonly CommandRegistry m_registry;
    private readonly IFixedStepSimulation? m_simulation;
    private readonly ISnapshotInputCapture[] m_snapshotInputCaptures;
    private readonly TerminalControl m_terminal;
    private readonly TextCommandSource m_textSource;

    public OffscreenTickHostedService(
        IHostApplicationLifetime applicationLifetime,
        BufferedConsoleOutput bufferedOutput,
        IInputClock inputClock,
        ILogger<OffscreenTickHostedService> logger,
        LauncherOptions options,
        OffscreenRenderOptions renderOptions,
        IRenderNode root,
        IHostContext rootHostContext,
        IEnumerable<InputRouter> inputRouters,
        IEnumerable<IFixedStepSimulation> simulations,
        IEnumerable<IPrecisionWaiter> precisionWaiters,
        IEnumerable<ISnapshotInputCapture> snapshotInputCaptures,
        CommandRegistry registry,
        TextCommandSource textSource,
        TerminalControl terminal
    ) {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(bufferedOutput);
        ArgumentNullException.ThrowIfNull(inputClock);
        ArgumentNullException.ThrowIfNull(inputRouters);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(renderOptions);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootHostContext);
        ArgumentNullException.ThrowIfNull(precisionWaiters);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(snapshotInputCaptures);
        ArgumentNullException.ThrowIfNull(textSource);
        ArgumentNullException.ThrowIfNull(terminal);

        m_applicationLifetime = applicationLifetime;
        m_bufferedOutput = bufferedOutput;
        m_inputClock = inputClock;
        m_inputRouter = LauncherHostLoop.SingleOrDefault(
            items: inputRouters,
            name: nameof(InputRouter),
            hostDescription: "offscreen host"
        );
        m_logger = logger;
        m_options = options;
        m_precisionWaiter = precisionWaiters.FirstOrDefault();
        m_renderOptions = renderOptions;
        m_root = root;
        m_rootHostContext = rootHostContext;
        m_registry = registry;
        m_snapshotInputCaptures = [.. snapshotInputCaptures];
        m_textSource = textSource;
        m_simulation = LauncherHostLoop.SingleOrDefault(
            items: simulations,
            name: nameof(IFixedStepSimulation),
            hostDescription: "offscreen host"
        );
        m_terminal = terminal;

        if ((m_simulation is null) != (m_inputRouter is null)) {
            throw new InvalidOperationException(message: "A fixed-step simulation and its InputRouter must be registered together. Use AddFixedStepSimulation<TSimulation>().");
        }

        m_registry.RouteSimulationTo(sink: m_inputRouter?.ConsoleTextSink);
    }

    private void RunOffscreenLoop(CancellationToken stoppingToken) {
        try {
            if (m_logger.IsEnabled(logLevel: LogLevel.Information)) {
                m_logger.LogInformation(message: "Offscreen boot: a real GPU device and the composed-frame render pipeline — no window, no swapchain.");
            }

            var clock = TickClock.Start();
            var pump = (((m_simulation is { } pumpSimulation) && (m_inputRouter is { } pumpInputRouter))
                ? new FixedStepPump(
                    simulation: pumpSimulation,
                    inputRouter: pumpInputRouter,
                    registry: m_registry,
                    captureOriginTicks: m_inputClock.NowTicks
                )
                : null
            );
            var frequency = Stopwatch.Frequency;
            var maxFrameTicks = (EngineTicks.PerSecond / 4UL);
            static uint ResolveRatePerSecond(IFixedStepSimulation? simulation) {
                var simRatePerSecond = (simulation?.RatePerSecond ?? LauncherHostLoop.DefaultUpdateRate);

                return ((simRatePerSecond == 0U)
                    ? LauncherHostLoop.DefaultUpdateRate
                    : simRatePerSecond
                );
            }

            var spinThreshold = LauncherHostLoop.SpinThreshold(frequency: frequency);
            var hostFrame = 0UL;
            var nextDeadline = Stopwatch.GetTimestamp();
            var exitAfterTimestamp = ((m_options.ExitAfter is { } exitAfter)
                ? (nextDeadline + ((long)(exitAfter.TotalSeconds * frequency)))
                : (long?)null
            );

            while (!stoppingToken.IsCancellationRequested) {
                m_textSource.Collect();
                m_bufferedOutput.Flush();

                if (
                    (exitAfterTimestamp is { } deadline) &&
                    (Stopwatch.GetTimestamp() >= deadline)
                ) {
                    m_terminal.RequestExit();
                }

                if (m_terminal.TryConsumeExit()) {
                    break;
                }

                for (var captureIndex = 0; (captureIndex < m_snapshotInputCaptures.Length); captureIndex++) {
                    m_snapshotInputCaptures[captureIndex].CaptureFrame(frameKey: hostFrame);
                }

                hostFrame++;

                var deltaTicks = clock.Sample();
                var ratePerSecond = ResolveRatePerSecond(simulation: m_simulation);
                var stepTicks = EngineTicks.PerRate(ratePerSecond: ratePerSecond);
                var period = (frequency / ((long)ratePerSecond));

                var stepsAdvanced = (pump?.Advance(
                    deltaTicks: deltaTicks,
                    maxFrameTicks: maxFrameTicks,
                    stepTicks: stepTicks
                ) ?? 0);

                m_bufferedOutput.Flush();

                // No presenter, no swapchain: produce the composed frame directly off the render root. A capture
                // armed by world.screenshot is served from inside this call (SdfEngineNode's own readback), so the
                // returned surface needs no further handling — it is simply not presented anywhere.
                var frameContext = new FrameContext(
                    AccumulatorTicks: (pump?.AccumulatorTicks ?? 0UL),
                    DeltaTicks: (((ulong)stepsAdvanced) * stepTicks),
                    ElapsedTicks: (pump?.ElapsedTicks ?? 0UL),
                    FrameDeltaTicks: deltaTicks,
                    Host: m_rootHostContext,
                    StepTicks: stepTicks,
                    TargetHeight: m_renderOptions.Height,
                    TargetWidth: m_renderOptions.Width
                );

                _ = m_root.ProduceFrame(context: in frameContext);

                m_bufferedOutput.Flush();

                nextDeadline += period;

                var nowTimestamp = Stopwatch.GetTimestamp();

                if ((nowTimestamp - nextDeadline) > period) {
                    nextDeadline = nowTimestamp;
                } else {
                    LauncherHostLoop.WaitUntil(
                        deadlineTimestamp: nextDeadline,
                        frequency: frequency,
                        precisionWaiter: m_precisionWaiter,
                        spinThreshold: spinThreshold
                    );
                }
            }

            m_logger.LogInformation(message: "Offscreen run ending; shutting the host down.");
        } finally {
            m_bufferedOutput.Flush();

            if (m_rootHostContext.TryResolveCapability<IGpuDeviceContext>(capability: out var deviceContext)) {
                deviceContext.WaitIdle();
            }

            m_root.Dispose();

            m_applicationLifetime.StopApplication();
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) {
        var pumpThread = new Thread(start: () => RunOffscreenLoop(stoppingToken: stoppingToken)) {
            IsBackground = true,
            Name = "Puck.Launcher Offscreen Tick Pump",
        };

        pumpThread.Start();

        return Task.CompletedTask;
    }
}
