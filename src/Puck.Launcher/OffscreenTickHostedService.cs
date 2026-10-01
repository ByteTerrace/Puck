using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Pacing;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>
/// The offscreen boot shape's outermost host loop — a real GPU device and the composed-frame render pipeline with NO
/// window and NO swapchain. Its frames are its only output, so its time is its tick count, never the wall clock: each
/// iteration runs at most one step of the SAME <see cref="FixedStepPump"/> the other hosts drive
/// (<see cref="FixedStepPump.TryStep"/>) and composes the frame that step owes (<see cref="ComposesFrame"/>), so every
/// tick composes and renders exactly one frame, whatever a frame cost. A slow frame (the first render, a rebuild, a
/// hitch) delays the next tick rather than bursting several ticks into one iteration, so a script that waits N ticks
/// has N frames rendered behind it, and a frame reprojects from the frame of the tick before. The wall clock only
/// keeps the loop from running faster than the simulation's rate; it decides no step, and each frame's presentation
/// interval is the simulation time it advanced. The console pump and every registered
/// <see cref="ISnapshotInputCapture"/> contribution run every iteration exactly like the other two host loops.
/// <para>Its pump holds its clock for its frames (<see cref="IFixedStepSimulation.HoldsClock"/>): while a frame a step
/// owes has not been served, for whatever reason the render chain cannot serve it yet, the loop keeps producing frames
/// and draining the console but steps no further tick. Those frames are the one exception to one frame per step: the
/// owed frame is composed again, advancing no simulation time, until one serves it.</para>
/// <para>The host reads its wall clock through a <see cref="TimeProvider"/>: <see cref="TimeProvider.System"/> unless
/// the container registers one, which is how a law runs the real loop over frames it makes slow.</para>
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
    private readonly IRenderRoot m_root;
    private readonly IHostContext m_rootHostContext;
    private readonly IFixedStepSimulation? m_simulation;
    private readonly ISnapshotInputCapture[] m_snapshotInputCaptures;
    private readonly TerminalControl m_terminal;
    private readonly TextCommandSource m_textSource;
    private readonly TimeProvider m_time;

    public OffscreenTickHostedService(
        IHostApplicationLifetime applicationLifetime,
        BufferedConsoleOutput bufferedOutput,
        IInputClock inputClock,
        ILogger<OffscreenTickHostedService> logger,
        LauncherOptions options,
        OffscreenRenderOptions renderOptions,
        IRenderRoot root,
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
        IEnumerable<GpuCreationFaults> faults,
        TimeProvider time
    ) {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(bufferedOutput);
        ArgumentNullException.ThrowIfNull(time);
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
        m_time = time;

        if ((m_simulation is null) != (m_inputRouter is null)) {
            throw new InvalidOperationException(message: "A fixed-step simulation and its InputRouter must be registered together. Use AddFixedStepSimulation<TSimulation>().");
        }

        m_registry.RouteSimulationTo(sink: m_inputRouter?.ConsoleTextSink);
    }

    /// <summary>Returns whether the offscreen host composes a frame after an iteration: one that stepped the simulation,
    /// or one that stepped nothing while a step still owes a frame (<see cref="IFixedStepSimulation.AwaitsFrame"/>, a
    /// capture armed at its tick not yet served), so every tick composes one frame and the host never steps past an owed
    /// frame. A host with no simulation composes a frame every iteration.</summary>
    /// <param name="hasSimulation">Whether the host steps a simulation.</param>
    /// <param name="stepped">Whether the iteration ran its step.</param>
    /// <param name="awaitsFrame">Whether the simulation owes a frame after the iteration.</param>
    /// <returns><see langword="true"/> when the host composes a frame.</returns>
    public static bool ComposesFrame(bool hasSimulation, bool stepped, bool awaitsFrame) => (
        !hasSimulation ||
        stepped ||
        awaitsFrame
    );

    private void RunOffscreenLoop(CancellationToken stoppingToken) {
        Exception? fault = null;

        try {
            if (m_logger.IsEnabled(logLevel: LogLevel.Information)) {
                m_logger.LogInformation(message: "Offscreen boot: a real GPU device and the composed-frame render pipeline — no window, no swapchain.");
            }

            var time = m_time;
            var clock = TickClock.Start(time: time);
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
            var frequency = time.TimestampFrequency;
            var spinThreshold = LauncherHostLoop.SpinThreshold(frequency: frequency);

            var deviceLoss = new DeviceLossRecovery(
                logger: m_logger,
                root: m_root,
                rootHostContext: m_rootHostContext,
                sleep: duration => LauncherHostLoop.WaitUntil(
                    deadlineTimestamp: (time.GetTimestamp() + ((long)(duration.TotalSeconds * frequency))),
                    precisionWaiter: m_precisionWaiter,
                    spinThreshold: spinThreshold,
                    time: time
                ),
                time: time,
                writeLine: m_bufferedOutput.WriteErrorLine
            );
            var hostFrame = 0UL;
            var nextDeadline = time.GetTimestamp();
            var exitAfterTimestamp = ((m_options.ExitAfter is { } exitAfter)
                ? (nextDeadline + ((long)(exitAfter.TotalSeconds * frequency)))
                : (long?)null
            );

            while (!stoppingToken.IsCancellationRequested) {
                m_textSource.Collect();
                m_bufferedOutput.Flush();

                if (
                    (exitAfterTimestamp is { } deadline) &&
                    (time.GetTimestamp() >= deadline)
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

                var intervalTicks = clock.Sample();
                var ratePerSecond = LauncherHostLoop.ResolveRatePerSecond(simulation: m_simulation);
                var stepTicks = EngineTicks.PerRate(ratePerSecond: ratePerSecond);
                var period = (frequency / ((long)ratePerSecond));

                // One step at most, whatever the iteration's interval was: the interval only rebases the input pin and
                // is the host time a hold withholds.
                var stepped = (pump?.TryStep(
                    intervalTicks: intervalTicks,
                    stepTicks: stepTicks
                ) ?? false);

                m_bufferedOutput.Flush();

                // No presenter, no swapchain: produce the composed frame directly off the render root, once for the
                // step just run, or again for a frame a step still owes. A capture armed by world.screenshot is served
                // from inside this call, so the returned surface needs no further handling — it is simply not presented
                // anywhere.
                if (ComposesFrame(
                    awaitsFrame: (m_simulation?.AwaitsFrame ?? false),
                    hasSimulation: (pump is not null),
                    stepped: stepped
                )) {
                    // The frame spans the simulation time its step advanced: one step, or none for an owed frame composed
                    // again. A host with no simulation spans one step of its own rate a frame.
                    var deltaTicks = (stepped
                        ? stepTicks
                        : 0UL);
                    // Read once, so a resize between frames reaches the whole frame and never half of it.
                    var (targetWidth, targetHeight) = m_renderOptions.Extent;
                    var frameContext = new FrameContext(
                        AccumulatorTicks: 0UL,
                        DeltaTicks: deltaTicks,
                        ElapsedTicks: (pump?.ElapsedTicks ?? 0UL),
                        FrameDeltaTicks: ((pump is null)
                            ? stepTicks
                            : deltaTicks),
                        Host: m_rootHostContext,
                        StepTicks: stepTicks,
                        TargetHeight: targetHeight,
                        TargetWidth: targetWidth
                    );

                    // Recovery refuses captures on the lost device, but the completed step still owes its frame.
                    // Retry that same context before another step; an unrecoverable loss faults the run.
                    while (true) {
                        try {
                            m_faults?.ThrowIfLossDue();
                            _ = m_root.ProduceFrame(context: in frameContext);
                            deviceLoss.NoteFrameProduced();

                            break;
                        } catch (DeviceLostException deviceLost) {
                            if (!deviceLoss.TryRecover(
                                deviceLost: deviceLost,
                                rebuild: m_deviceRebuild
                            )) {
                                throw;
                            }

                            m_bufferedOutput.Flush();
                        }
                    }

                    m_bufferedOutput.Flush();
                }

                // The wall clock only keeps the loop from outrunning the simulation's rate. A late iteration owes no
                // steps: past a whole period late, the grid re-origins at now.
                nextDeadline += period;

                var nowTimestamp = time.GetTimestamp();

                if ((nowTimestamp - nextDeadline) > period) {
                    nextDeadline = nowTimestamp;
                } else {
                    LauncherHostLoop.WaitUntil(
                        deadlineTimestamp: nextDeadline,
                        precisionWaiter: m_precisionWaiter,
                        spinThreshold: spinThreshold,
                        time: time
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
