using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Pacing;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;

namespace Puck.Launcher;

/// <summary>The common iteration handed to a presentation adapter.</summary>
/// <param name="TickLocked">Whether the iteration stepped at most one tick for its frame, read once as the iteration began.</param>
/// <param name="IntervalTicks">The wall interval the iteration sampled, in engine ticks.</param>
/// <param name="StepTicks">The fixed step, in engine ticks.</param>
/// <param name="ElapsedTicks">The engine ticks the pump has consumed.</param>
/// <param name="Steps">The whole steps the iteration ran.</param>
/// <param name="HasSimulation">Whether the host steps a simulation.</param>
/// <param name="Holding">Whether the previous frame left its tick owing a rendered frame.</param>
/// <param name="StepsNext">Whether the next iteration is expected to run a step: under lockstep whenever the host steps
/// a simulation, otherwise when the pump's held remainder and one more interval like this one reach a step
/// (<see cref="FrameContext.StepsNext"/>).</param>
public readonly record struct HostIteration(bool TickLocked, ulong IntervalTicks, ulong StepTicks, ulong ElapsedTicks, int Steps, bool HasSimulation, bool Holding, bool StepsNext = false) {
    /// <summary>Gets the simulation tick the pump has consumed through.</summary>
    public long Tick => ((StepTicks == 0UL) ? 0L : ((long)(ElapsedTicks / StepTicks)));
}
/// <summary>Owns input contributions, console drains, fixed steps, frame holds, deadlines and pacing for every
/// launcher registration. Presentation adapters supply window input, frame production and display waits.</summary>
public sealed class HostLoop(
    IHostApplicationLifetime lifetime, BufferedConsoleOutput output, IInputClock inputClock,
    LauncherOptions options, IEnumerable<InputRouter> routers, IEnumerable<IFixedStepSimulation> simulations,
    IEnumerable<ISnapshotInputCapture> captures, IEnumerable<IPrecisionWaiter> waiters, CommandRegistry registry,
    TextCommandSource textSource, TerminalControl terminal, StandardInputBacklog backlog, TimeProvider time,
    HostFrameTimeline timeline, HostPacing pacing, ILogger<HostLoop> logger
) {
    private readonly InputRouter? m_router = LauncherHostLoop.SingleOrDefault(routers, nameof(InputRouter), "host");
    private readonly IFixedStepSimulation? m_simulation = LauncherHostLoop.SingleOrDefault(simulations, nameof(IFixedStepSimulation), "host");
    private readonly ISnapshotInputCapture[] m_captures = [.. captures];
    private readonly IPrecisionWaiter? m_waiter = waiters.FirstOrDefault();

    /// <summary>Whether a completed tick still owes a renderable frame.</summary>
    public static bool HoldsTick(bool hasSimulation, FrameCompletion completion) => (hasSimulation && (completion == FrameCompletion.NotYetRenderable));
    /// <summary>Runs inline on the hosted service's existing pump thread. No simulation thread is started here.</summary>
    public void Run(CancellationToken stoppingToken, Func<bool>? isOpen = null,
        Action<ulong>? captureInput = null,
        Func<HostIteration, bool>? produce = null, Action? wait = null) {
        if ((m_simulation is null) != (m_router is null)) {
            throw new InvalidOperationException(message: "A fixed-step simulation and its InputRouter must be registered together. Use AddFixedStepSimulation<TSimulation>().");
        }
        registry.RouteSimulationTo(sink: m_router?.ConsoleTextSink);
        var pump = FixedStepPump.CreateHosted(m_simulation, m_router, registry, inputClock, textSource, output, terminal, backlog,
            holdsClock: (produce is not null), timeline: timeline);
        var clock = TickClock.Start(time: time);
        var frequency = time.TimestampFrequency;
        var nextDeadline = time.GetTimestamp();
        var exitAt = ((options.ExitAfter is { } duration) ? (nextDeadline + ((long)(duration.TotalSeconds * frequency))) : (long?)null);
        var frame = 0UL;
        var holding = false;
        Exception? fault = null;

        try {
            while (!stoppingToken.IsCancellationRequested && (isOpen?.Invoke() ?? true)) {
                timeline.BeginIteration();
                captureInput?.Invoke(frame);
                timeline.Mark(phase: HostFramePhase.Input);
                // The pump owns the drain even on zero-step iterations and while a frame holds the tick.
                // The first step reuses it; additional catch-up steps drain between ticks as before.
                if (pump is not null) {
                    pump.BeginIteration();
                } else {
                    HostConsoleDrain.Collect(output: output, source: textSource);
                }
                if ((exitAt is { } deadline) && (time.GetTimestamp() >= deadline)) {
                    terminal.RequestExit();
                }
                if (terminal.TryConsumeExit()) {
                    break;
                }
                foreach (var capture in m_captures) {
                    capture.CaptureFrame(frameKey: frame);
                }
                frame++;
                timeline.Mark(phase: HostFramePhase.Commands);

                // Read once: a lockstep switch inside this iteration's drain takes effect on the next one, so the step, the
                // hold and the adapter's frame all agree on one mode.
                var tickLocked = pacing.StepsOneTickPerFrame;
                var stepTicks = FixedTickConversion.PerRate(ratePerSecond: LauncherHostLoop.ResolveRatePerSecond(simulation: m_simulation));
                var interval = clock.Sample();
                var steps = 0;

                if (!tickLocked) {
                    holding = false;
                }
                if (holding) {
                    pump?.Hold(intervalTicks: interval);
                } else if (tickLocked) {
                    steps = ((pump?.TryStep(intervalTicks: interval, stepTicks: stepTicks) == true) ? 1 : 0);
                } else {
                    steps = (pump?.Advance(deltaTicks: interval, maxFrameTicks: (FixedTickConversion.TicksPerSecond / 4UL), stepTicks: stepTicks) ?? 0);
                }
                timeline.NoteSteps(steps: steps);
                timeline.Mark(phase: HostFramePhase.Simulate);
                output.Flush();
                timeline.Mark(phase: HostFramePhase.Commands);

                if (produce is not null) {
                    var stepsNext = ((pump is not null) && (tickLocked || ((pump.AccumulatorTicks + interval) >= stepTicks)));

                    holding = produce(new HostIteration(tickLocked, interval, stepTicks, (pump?.ElapsedTicks ?? 0UL), steps, (pump is not null), holding, stepsNext));
                }
                if (terminal.TryConsumeExit()) {
                    break;
                }
                if (wait is not null) {
                    wait();
                } else if (!pacing.IsUnpaced) {
                    var period = (frequency / ((long)LauncherHostLoop.ResolveRatePerSecond(simulation: m_simulation)));

                    nextDeadline += period;
                    var now = time.GetTimestamp();

                    if ((now - nextDeadline) > period) {
                        nextDeadline = now;
                    } else {
                        LauncherHostLoop.WaitUntil(deadlineTimestamp: nextDeadline, precisionWaiter: m_waiter, time: time);
                        timeline.NoteLate(ticks: Math.Max(val1: 0L, val2: (time.GetTimestamp() - nextDeadline)));
                    }
                    timeline.Mark(phase: HostFramePhase.Wait);
                }
            }
        } catch (Exception exception) {
            fault = exception;
            throw;
        } finally {
            try {
                LauncherHostRun.RunTeardown(fault, logger, ("flush output", output.Flush));
            } finally {
                if (produce is null) {
                    lifetime.StopApplication();
                }
            }
        }
    }
}
