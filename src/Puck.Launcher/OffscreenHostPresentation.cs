using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Pacing;
using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>Offscreen frame production and device recovery for the shared host loop.</summary>
public sealed class OffscreenHostPresentation {
    private readonly IHostApplicationLifetime m_applicationLifetime;
    private readonly BufferedConsoleOutput m_bufferedOutput;
    private readonly IDeviceRebuild? m_deviceRebuild;
    private readonly GpuCreationFaults? m_faults;
    private readonly GpuFrameCapture? m_gpuCapture;
    private readonly ILogger<OffscreenHostPresentation> m_logger;
    private readonly IPrecisionWaiter? m_precisionWaiter;
    private readonly OffscreenRenderOptions m_renderOptions;
    private readonly IRenderRoot m_root;
    private readonly IHostContext m_rootHostContext;
    private readonly IFixedStepSimulation? m_simulation;
    private readonly TimeProvider m_time;
    private readonly HostFrameTimeline m_timeline;

    public OffscreenHostPresentation(
        IHostApplicationLifetime applicationLifetime,
        BufferedConsoleOutput bufferedOutput,
        ILogger<OffscreenHostPresentation> logger,
        OffscreenRenderOptions renderOptions,
        IRenderRoot root,
        IHostContext rootHostContext,
        IEnumerable<IFixedStepSimulation> simulations,
        IEnumerable<IPrecisionWaiter> precisionWaiters,
        IEnumerable<IDeviceRebuild> deviceRebuilds,
        IEnumerable<GpuCreationFaults> faults,
        IEnumerable<GpuFrameCapture> gpuCaptures,
        TimeProvider time,
        HostFrameTimeline timeline
    ) {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(bufferedOutput);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(deviceRebuilds);
        ArgumentNullException.ThrowIfNull(faults);
        ArgumentNullException.ThrowIfNull(gpuCaptures);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(renderOptions);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootHostContext);
        ArgumentNullException.ThrowIfNull(precisionWaiters);

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
        m_gpuCapture = LauncherHostLoop.SingleOrDefault(
            items: gpuCaptures,
            name: nameof(GpuFrameCapture),
            hostDescription: "offscreen host"
        );
        m_logger = logger;
        m_precisionWaiter = precisionWaiters.FirstOrDefault();
        m_renderOptions = renderOptions;
        m_root = root;
        m_rootHostContext = rootHostContext;
        m_simulation = LauncherHostLoop.SingleOrDefault(
            items: simulations,
            name: nameof(IFixedStepSimulation),
            hostDescription: "offscreen host"
        );
        m_time = time;
        m_timeline = timeline;

    }

    public void Run(HostLoop loop, CancellationToken stoppingToken) {
        Exception? fault = null;

        try {
            if (m_logger.IsEnabled(logLevel: LogLevel.Information)) {
                m_logger.LogInformation(message: "Offscreen boot: a real GPU device and the composed-frame render pipeline — no window, no swapchain.");
            }

            var time = m_time;
            var frequency = time.TimestampFrequency;

            var deviceLoss = new DeviceLossRecovery(
                logger: m_logger,
                root: m_root,
                rootHostContext: m_rootHostContext,
                sleep: duration => LauncherHostLoop.WaitUntil(
                    deadlineTimestamp: (time.GetTimestamp() + ((long)(duration.TotalSeconds * frequency))),
                    precisionWaiter: m_precisionWaiter,
                    time: time
                ),
                time: time,
                writeLine: m_bufferedOutput.WriteErrorLine
            );
            var composedTick = false;

            loop.Run(stoppingToken: stoppingToken, produce: iteration => {
                // A host whose time is its tick count composes a frame only when one is owed: a tick just stepped, the
                // previous frame holds its tick, or the simulation awaits a frame (a capture, a pending bake).
                if (
                    iteration.TickLocked &&
                    iteration.HasSimulation &&
                    (iteration.Steps == 0) &&
                    !iteration.Holding &&
                    !(m_simulation?.AwaitsFrame ?? false)
                ) {
                    return false;
                }

                var stepTicks = iteration.StepTicks;

                if (iteration.Steps > 0) { composedTick = false; }
                // The tick's first composition spans the step it advanced; composing it again, until it renders or a
                // capture is served, advances nothing, so presentation moves once a tick however many attempts its
                // frame takes. A host with no simulation spans one step of its own rate a frame.
                var deltaTicks = ((iteration.HasSimulation && !composedTick)
                    ? stepTicks
                    : 0UL);

                // Read once, so a resize between frames reaches the whole frame and never half of it.
                var (targetWidth, targetHeight) = m_renderOptions.Extent;
                var frameContext = new FrameContext(
                    AccumulatorTicks: 0UL,
                    DeltaTicks: deltaTicks,
                    ElapsedTicks: iteration.ElapsedTicks,
                    FrameDeltaTicks: (!iteration.HasSimulation
                        ? stepTicks
                        : deltaTicks),
                    Host: m_rootHostContext,
                    StepTicks: stepTicks,
                    Tick: iteration.Tick,
                    TargetHeight: targetHeight,
                    TargetWidth: targetWidth
                );

                // A frame the root has not rendered holds the tick: the next iteration composes it again and steps
                // nothing. A device loss is the same: recovery refuses captures on the lost device, and the completed
                // step still owes its frame. An unrecoverable loss faults the run.
                var wasHolding = iteration.Holding;
                var holding = false;
                string? reason;

                try {
                    DeviceLossRecovery.CountFrame(faults: m_faults, rootHostContext: m_rootHostContext);
                    // The tick counts as composed once the root has been handed its context, the step it advanced
                    // included, so a loss raised before the hand-off leaves the retry carrying the step and one the
                    // root raises after it leaves the retry advancing nothing.
                    composedTick = true;
                    // The operator's gpu.capture brackets exactly the frame's GPU work.
                    m_gpuCapture?.BeginFrame();

                    var produced = m_root.ProduceFrame(context: in frameContext);

                    m_gpuCapture?.EndFrame();
                    deviceLoss.NoteFrameProduced();
                    holding = HostLoop.HoldsTick(
                        completion: produced.Render.Completion,
                        hasSimulation: iteration.HasSimulation
                    );
                    reason = produced.Render.Reason;
                } catch (DeviceLostException deviceLost) {
                    if (!deviceLoss.TryRecover(
                        deviceLost: deviceLost,
                        rebuild: m_deviceRebuild
                    )) {
                        throw;
                    }

                    holding = iteration.HasSimulation;
                    reason = "the device was lost and rebuilt";
                }

                m_timeline.Mark(phase: HostFramePhase.Produce);
                // Each hold is narrated once, as it starts, so a run that never renders names why.
                if (
                    holding &&
                    !wasHolding
                ) {
                    m_bufferedOutput.WriteErrorLine(value: $"[offscreen] holding tick {(frameContext.ElapsedTicks / stepTicks)} until its frame renders: {reason}");
                }

                m_bufferedOutput.Flush();
                return holding;
            });

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

}
