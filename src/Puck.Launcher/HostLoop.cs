using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Pacing;
using Puck.Abstractions.Waiting;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;

namespace Puck.Launcher;

/// <summary>
/// The common iteration handed to a presentation adapter's produce, minted only by the loop's control phase.
/// <para>
/// It is a <see langword="ref"/> struct, so the compiler keeps it from being stored in a field, captured by a lambda or
/// kept past the produce call it was passed to: no adapter can replay an iteration from an earlier callback. Its
/// constructor checks the loop's own phase state (a control phase completed for this iteration and no iteration minted
/// since), so a mint outside that window throws whoever calls it, and a <see langword="default"/> iteration, which no
/// control phase minted, throws when read.
/// </para>
/// </summary>
public readonly ref struct HostIteration {
    private readonly bool m_minted;
    private readonly bool m_tickLocked;
    private readonly ulong m_intervalTicks;
    private readonly ulong m_stepTicks;
    private readonly ulong m_elapsedTicks;
    private readonly int m_steps;
    private readonly bool m_hasSimulation;
    private readonly bool m_holding;
    private readonly bool m_stepsNext;

    /// <summary>Initializes a new instance of the <see cref="HostIteration"/> struct for the iteration whose control phase
    /// <paramref name="loop"/> has just run.</summary>
    /// <param name="loop">The loop whose control phase mints the iteration.</param>
    /// <param name="tickLocked">Whether the iteration stepped at most one tick for its frame, read once as the iteration
    /// began.</param>
    /// <param name="intervalTicks">The wall interval the iteration sampled, in engine ticks.</param>
    /// <param name="stepTicks">The fixed step, in engine ticks.</param>
    /// <param name="elapsedTicks">The engine ticks the pump has consumed.</param>
    /// <param name="steps">The whole steps the iteration ran.</param>
    /// <param name="hasSimulation">Whether the host steps a simulation.</param>
    /// <param name="holding">Whether the previous frame left its tick owing a rendered frame.</param>
    /// <param name="stepsNext">Whether the next iteration is expected to run a step (<see cref="StepsNext"/>).</param>
    /// <exception cref="InvalidOperationException">No control phase of <paramref name="loop"/> has run since its last
    /// iteration was minted.</exception>
    internal HostIteration(HostLoop loop, bool tickLocked, ulong intervalTicks, ulong stepTicks, ulong elapsedTicks, int steps, bool hasSimulation, bool holding, bool stepsNext) {
        loop.Mint();
        m_minted = true;
        m_tickLocked = tickLocked;
        m_intervalTicks = intervalTicks;
        m_stepTicks = stepTicks;
        m_elapsedTicks = elapsedTicks;
        m_steps = steps;
        m_hasSimulation = hasSimulation;
        m_holding = holding;
        m_stepsNext = stepsNext;
    }

    /// <summary>Gets whether the iteration stepped at most one tick for its frame, read once as the iteration began.</summary>
    public bool TickLocked => (Minted() && m_tickLocked);
    /// <summary>Gets the wall interval the iteration sampled, in engine ticks.</summary>
    public ulong IntervalTicks => (Minted() ? m_intervalTicks : 0UL);
    /// <summary>Gets the fixed step, in engine ticks.</summary>
    public ulong StepTicks => (Minted() ? m_stepTicks : 0UL);
    /// <summary>Gets the engine ticks the pump has consumed.</summary>
    public ulong ElapsedTicks => (Minted() ? m_elapsedTicks : 0UL);
    /// <summary>Gets the whole steps the iteration ran.</summary>
    public int Steps => (Minted() ? m_steps : 0);
    /// <summary>Gets whether the host steps a simulation.</summary>
    public bool HasSimulation => (Minted() && m_hasSimulation);
    /// <summary>Gets whether the previous frame left its tick owing a rendered frame.</summary>
    public bool Holding => (Minted() && m_holding);
    /// <summary>Gets whether the next iteration is expected to run a step: under lockstep whenever the host steps a
    /// simulation, otherwise when the pump's held remainder and one more interval like this one reach a step
    /// (<see cref="FrameContext.StepsNext"/>).</summary>
    public bool StepsNext => (Minted() && m_stepsNext);
    /// <summary>Gets the simulation tick the pump has consumed through.</summary>
    public long Tick => ((StepTicks == 0UL) ? 0L : ((long)(ElapsedTicks / StepTicks)));

    private bool Minted() => (m_minted ? true : throw new InvalidOperationException(message: "This host iteration was not minted by a control phase."));
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

    /// <summary>Gets the platform's high-resolution waiter the pacer sleeps on, or <see langword="null"/> where the host
    /// registered none. The presentation adapters take their deadline waits (<see cref="HostWaits.Until"/>) from it too,
    /// so one waiter serves the whole pump thread.</summary>
    public IPrecisionWaiter? PrecisionWaiter => m_waiter;

    // The control phases run and the iterations minted, on the pump thread: an iteration is minted at most once per
    // control phase (HostIteration's constructor calls Mint).
    private long m_controlPhases;
    private long m_minted;
    // The step rate the iteration resolved after its control phase, in steps a second: the iteration's step width and
    // pacer period, and the next control phases' wait allowance. A rate a console drain or a step changes is read by
    // the next iteration; the pump ends a burst on the change itself (FixedStepPump.Advance).
    private uint m_ratePerSecond = LauncherHostLoop.DefaultUpdateRate;

    /// <summary>Whether a completed tick still owes a renderable frame.</summary>
    public static bool HoldsTick(bool hasSimulation, FrameCompletion completion) => (hasSimulation && (completion == FrameCompletion.NotYetRenderable));
    // The control phase: the console drain, the control gap's mark, and a fresh wait allowance for the pump thread of
    // one pacer period, so the waits it takes through HostWaits before the next control phase block for at most that.
    // The console drains only at a tick boundary; while a step is held across wait slices its lines wait for it.
    private void ControlPhase(bool atBoundary) {
        if (atBoundary) { HostConsoleDrain.Collect(output: output, source: textSource); }
        timeline.NoteControlPhase();
        HostWaits.BeginPumpIteration(allowance: TimeSpan.FromSeconds(value: (1.0 / m_ratePerSecond)));
        m_controlPhases++;
    }
    internal void Mint() {
        if (m_minted >= m_controlPhases) {
            throw new InvalidOperationException(message: "A host iteration is minted only once per control phase, by the loop that ran it.");
        }
        m_minted = m_controlPhases;
    }
    /// <summary>Runs inline on the hosted service's existing pump thread. No simulation thread is started here.</summary>
    public void Run(CancellationToken stoppingToken, Func<bool>? isOpen = null,
        Action<ulong>? captureInput = null,
        Func<HostIteration, bool>? produce = null, Action? wait = null) {
        if ((m_simulation is null) != (m_router is null)) {
            throw new InvalidOperationException(message: "A fixed-step simulation and its InputRouter must be registered together. Use AddFixedStepSimulation<TSimulation>().");
        }
        registry.RouteSimulationTo(sink: m_router?.ConsoleTextSink);
        m_ratePerSecond = LauncherHostLoop.ResolveRatePerSecond(simulation: m_simulation);
        var pump = FixedStepPump.CreateHosted(m_simulation, m_router, registry, inputClock, textSource, ControlPhase, terminal, backlog,
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
                    ControlPhase(atBoundary: true);
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
                m_ratePerSecond = LauncherHostLoop.ResolveRatePerSecond(simulation: m_simulation);

                var stepTicks = FixedTickConversion.PerRate(ratePerSecond: m_ratePerSecond);
                var interval = clock.Sample();
                var steps = 0;

                using (timeline.FrameWork.Bind()) {
                    if (!tickLocked) {
                        holding = false;
                    }
                    if (holding) {
                        pump?.Hold(intervalTicks: interval);
                    } else if (tickLocked) {
                        steps = ((pump?.TryStep(intervalTicks: interval, stepTicks: stepTicks) == true) ? 1 : 0);
                    } else {
                        steps = (pump?.Advance(deltaTicks: interval, maxFrameTicks: FixedTickConversion.PerRate(ratePerSecond: 4U), stepTicks: stepTicks) ?? 0);
                    }
                }
                timeline.NoteSteps(steps: steps);
                timeline.Mark(phase: HostFramePhase.Simulate);
                timeline.Mark(phase: HostFramePhase.Commands);

                if (produce is not null && pump?.StepPending != true) {
                    var stepsNext = ((pump is not null) && (tickLocked || ((pump.AccumulatorTicks + interval) >= stepTicks)));

                    holding = produce(new HostIteration(
                        elapsedTicks: (pump?.ElapsedTicks ?? 0UL),
                        hasSimulation: (pump is not null),
                        holding: holding,
                        intervalTicks: interval,
                        loop: this,
                        steps: steps,
                        stepsNext: stepsNext,
                        stepTicks: stepTicks,
                        tickLocked: tickLocked
                    ));
                }
                if (terminal.TryConsumeExit()) {
                    break;
                }
                if (wait is not null) {
                    wait();
                } else if (!pacing.IsUnpaced || pump?.StepPending == true) {
                    var period = (frequency / ((long)m_ratePerSecond));

                    nextDeadline += period;
                    var now = time.GetTimestamp();

                    if ((now - nextDeadline) > period) {
                        nextDeadline = now;
                    } else {
                        HostWaits.Pace(deadlineTimestamp: nextDeadline, precisionWaiter: m_waiter, time: time);
                        timeline.NoteLate(ticks: Math.Max(val1: 0L, val2: (time.GetTimestamp() - nextDeadline)));
                    }
                    timeline.Mark(phase: HostFramePhase.Wait);
                }
            }
        } catch (Exception exception) {
            fault = exception;
            throw;
        } finally {
            HostWaits.EndPump();
            try {
                LauncherHostRun.RunTeardown(fault, logger, ("flush output", output.Drain));
            } finally {
                if (produce is null) {
                    lifetime.StopApplication();
                }
            }
        }
    }
}
