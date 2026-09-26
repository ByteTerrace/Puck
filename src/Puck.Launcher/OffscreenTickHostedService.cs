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
/// cadence to ride — composes a frame right after the fixed-step pump advances, and only when it stepped
/// (<see cref="ComposesFrame"/>): frame pacing rides the fixed-step pump's own cadence instead of vsync, and the host
/// renders at most one frame per step, so a frame never presents a tick twice for a presentation clock to tell apart.
/// The console pump and every registered <see cref="ISnapshotInputCapture"/> contribution run every iteration exactly
/// like the other two host loops.
/// <para>Its frames are its only output, so its pump holds its clock for them
/// (<see cref="IFixedStepSimulation.HoldsClock"/>): while a frame a step owes has not been served, for whatever reason
/// the render chain cannot serve it yet, the loop keeps producing frames and draining the console but steps no further
/// tick. Those frames are the one exception to one frame per step: the owed frame is composed again until one serves
/// it. The two other host loops never hold.</para>
/// <para>A device loss follows the windowed host's policy (<see cref="DeviceLossRecovery"/>), rebuilding through the
/// <see cref="IDeviceRebuild"/> the offscreen GPU activation registers; a loss it cannot recover from faults the
/// run.</para>
/// </summary>
public sealed class OffscreenTickHostedService : BackgroundService {
    private readonly IHostApplicationLifetime m_applicationLifetime;
    private readonly BufferedConsoleOutput m_bufferedOutput;
    private readonly IDeviceRebuild? m_deviceRebuild;
    private readonly GpuCreationFaults? m_faults;
    private readonly IInputClock m_inputClock;
    private readonly StandardInputBacklog m_inputBacklog;
    private readonly InputRouter? m_inputRouter;
    private readonly ILogger<OffscreenTickHostedService> m_logger;
    private readonly LauncherOptions m_options;
    private readonly IPrecisionWaiter? m_precisionWaiter;
    private readonly CommandRegistry m_registry;
    private readonly OffscreenRenderOptions m_renderOptions;
    private readonly IRenderNode m_root;
    private readonly IHostContext m_rootHostContext;
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
        TerminalControl terminal,
        StandardInputBacklog inputBacklog,
        IEnumerable<IDeviceRebuild> deviceRebuilds,
        IEnumerable<GpuCreationFaults> faults
    ) {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(bufferedOutput);
        ArgumentNullException.ThrowIfNull(deviceRebuilds);
        ArgumentNullException.ThrowIfNull(faults);
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
        ArgumentNullException.ThrowIfNull(inputBacklog);

        m_applicationLifetime = applicationLifetime;
        m_bufferedOutput = bufferedOutput;
        m_deviceRebuild = LauncherHostLoop.SingleOrDefault(
            items: deviceRebuilds,
            name: nameof(IDeviceRebuild),
            hostDescription: "offscreen host"
        );
        m_faults = LauncherHostLoop.SingleOrDefault(
            items: faults,
            name: nameof(GpuCreationFaults),
            hostDescription: "offscreen host"
        );
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
        m_inputBacklog = inputBacklog;

        if ((m_simulation is null) != (m_inputRouter is null)) {
            throw new InvalidOperationException(message: "A fixed-step simulation and its InputRouter must be registered together. Use AddFixedStepSimulation<TSimulation>().");
        }

        m_registry.RouteSimulationTo(sink: m_inputRouter?.ConsoleTextSink);
    }

    /// <summary>Returns whether the offscreen host composes a frame after a pump call: one that stepped the simulation, or
    /// one that stepped nothing while a step still owes a frame (<see cref="IFixedStepSimulation.AwaitsFrame"/>, a capture
    /// armed at its tick not yet served), so the host renders at most one frame per step and never steps past an owed
    /// frame. A host with no simulation composes a frame every call.</summary>
    /// <param name="hasSimulation">Whether the host steps a simulation.</param>
    /// <param name="stepsAdvanced">The steps the pump call ran.</param>
    /// <param name="awaitsFrame">Whether the simulation owes a frame after the call.</param>
    /// <returns><see langword="true"/> when the host composes a frame.</returns>
    public static bool ComposesFrame(bool hasSimulation, int stepsAdvanced, bool awaitsFrame) => (
        !hasSimulation ||
        (stepsAdvanced > 0) ||
        awaitsFrame
    );

    private void RunOffscreenLoop(CancellationToken stoppingToken) {
        Exception? fault = null;

        try {
            if (m_logger.IsEnabled(logLevel: LogLevel.Information)) {
                m_logger.LogInformation(message: "Offscreen boot: a real GPU device and the composed-frame render pipeline — no window, no swapchain.");
            }

            var clock = TickClock.Start();
            var pump = FixedStepPump.CreateHosted(
                holdsClock: true,
                inputBacklog: m_inputBacklog,
                inputClock: m_inputClock,
                inputRouter: m_inputRouter,
                output: m_bufferedOutput,
                registry: m_registry,
                simulation: m_simulation,
                terminal: m_terminal,
                textSource: m_textSource
            );
            var frequency = Stopwatch.Frequency;
            var maxFrameTicks = (EngineTicks.PerSecond / 4UL);

            var deviceLoss = new DeviceLossRecovery(
                logger: m_logger,
                root: m_root,
                rootHostContext: m_rootHostContext,
                writeLine: m_bufferedOutput.WriteErrorLine
            );
            var spinThreshold = LauncherHostLoop.SpinThreshold(frequency: frequency);
            var frameInterval = new OffscreenFrameInterval();
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
                var ratePerSecond = LauncherHostLoop.ResolveRatePerSecond(simulation: m_simulation);
                var stepTicks = EngineTicks.PerRate(ratePerSecond: ratePerSecond);
                var period = (frequency / ((long)ratePerSecond));

                var stepsAdvanced = (pump?.Advance(
                    deltaTicks: deltaTicks,
                    maxFrameTicks: maxFrameTicks,
                    stepTicks: stepTicks
                ) ?? 0);

                m_bufferedOutput.Flush();

                // No presenter, no swapchain: produce the composed frame directly off the render root, once for the
                // steps just run, or again for a frame a step still owes. A capture armed by world.screenshot is served
                // from inside this call, so the returned surface needs no further handling — it is simply not presented
                // anywhere.
                var composes = ComposesFrame(
                    awaitsFrame: (m_simulation?.AwaitsFrame ?? false),
                    hasSimulation: (pump is not null),
                    stepsAdvanced: stepsAdvanced
                );
                var frameDeltaTicks = frameInterval.Take(
                    composes: composes,
                    deltaTicks: deltaTicks,
                    maxFrameTicks: maxFrameTicks
                );

                if (composes) {
                    var frameContext = new FrameContext(
                        AccumulatorTicks: (pump?.AccumulatorTicks ?? 0UL),
                        DeltaTicks: (((ulong)stepsAdvanced) * stepTicks),
                        ElapsedTicks: (pump?.ElapsedTicks ?? 0UL),
                        FrameDeltaTicks: frameDeltaTicks,
                        Host: m_rootHostContext,
                        StepTicks: stepTicks,
                        TargetHeight: m_renderOptions.Height,
                        TargetWidth: m_renderOptions.Width
                    );

                    // A loss follows the windowed host's policy: captures armed at it are refused by name, the device
                    // is rebuilt in place, and the loop steps on. A loss it cannot recover from ends the run as a fault.
                    try {
                        // The operator's gpu.faults lose loses the device on its armed frame, here, like a real loss.
                        m_faults?.ThrowIfLossDue();
                        _ = m_root.ProduceFrame(context: in frameContext);
                        deviceLoss.NoteFrameProduced();
                    } catch (DeviceLostException deviceLost) {
                        if (!deviceLoss.TryRecover(
                            deviceLost: deviceLost,
                            rebuild: m_deviceRebuild
                        )) {
                            throw;
                        }

                        m_bufferedOutput.Flush();

                        continue;
                    }

                    m_bufferedOutput.Flush();
                }

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
        } catch (Exception exception) {
            fault = exception;

            throw;
        } finally {
            try {
                LauncherHostRun.RunTeardown(
                    fault,
                    m_logger,
                    ("flush output", m_bufferedOutput.Flush),
                    // Before the render root goes: a capture still owed a frame is decided while the chain that would
                    // have served it is alive, never refused by the disposal of a node still holding it.
                    ("settle owed frames", () => m_simulation?.SettleOwedFrames()),
                    ("drain device", () => {
                        if (m_rootHostContext.TryResolveCapability<IGpuDeviceContext>(capability: out var deviceContext)) {
                            deviceContext.WaitIdle();
                        }
                    }
                ),
                    ("dispose render root", m_root.Dispose)
                );
            } finally {
                m_applicationLifetime.StopApplication();
            }
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => LauncherHostLoop.RunPump(
        name: "Puck.Launcher Offscreen Tick Pump",
        pump: () => RunOffscreenLoop(stoppingToken: stoppingToken)
    );
}
