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
    /// <param name="steps">The whole steps collected since the previous iteration was minted.</param>
    /// <param name="hasSimulation">Whether the host steps a simulation.</param>
    /// <param name="holding">Whether the previous frame left its tick owing a rendered frame.</param>
    /// <param name="stepsNext">Whether the next iteration is expected to begin a step (<see cref="StepsNext"/>).</param>
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
    /// <summary>Gets the whole steps collected since the previous iteration was minted: those this iteration's step
    /// section completed, and those a step left executing beside an earlier frame completed after that frame was
    /// produced. The frame shows the state through the last of them.</summary>
    public int Steps => (Minted() ? m_steps : 0);
    /// <summary>Gets whether the host steps a simulation.</summary>
    public bool HasSimulation => (Minted() && m_hasSimulation);
    /// <summary>Gets whether the previous frame left its tick owing a rendered frame.</summary>
    public bool Holding => (Minted() && m_holding);
    /// <summary>Gets whether the next iteration is expected to begin a step: under lockstep whenever the host steps a
    /// simulation, otherwise when the pump's held remainder, less the step still in progress, and one more interval
    /// like this one reach a step (<see cref="FrameContext.StepsNext"/>).</summary>
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
    private static readonly WaitReason ResumeReason = new(Subsystem: "host", WaitsFor: "resuming the step in progress");

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
        // Whether this iteration goes on while its last due step executes on the simulation's own thread, instead of
        // waiting for it: every host does, with or without a frame, unless it steps one tick per frame, whose frame
        // shows exactly the tick it stepped.
        var beside = false;
        // The steps collected since the last frame was produced: what the next frame's iteration reports.
        var frameSteps = 0;
        // Set on the simulation's thread when a pending step can make progress, and taken by the service below.
        var progressed = 0;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? resumeFault = null;
        // Resumes the open step beside the frame: it collects what finished and hands over what follows in the same
        // step. After produce it also continues the burst (continueBurst, with the iteration's step width): each step
        // still due from the iteration's own interval begins after a control phase, from the iteration's own input
        // capture, as a later step of a burst does in the step section. The pacing wait begins no step, since no control
        // phase can run inside it. A step it completes is counted, and its time charged, in the iteration that
        // collected it.
        void ResumeOpenStep(HostFramePhase charged, ulong continueBurst = 0UL) {
            if (!beside || (resumeFault is not null) || (pump is not { StepPending: true })) {
                return;
            }

            timeline.Mark(phase: charged);
            try {
                using (timeline.FrameWork.Bind()) {
                    var collected = ((continueBurst != 0UL) ? pump.ContinueBurst(stepTicks: continueBurst) : (pump.Resume() ? 1 : 0));

                    timeline.NoteSteps(steps: collected);
                    frameSteps += collected;
                }
            } catch (Exception exception) {
                resumeFault = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(source: exception);
            }
            timeline.Mark(phase: HostFramePhase.Simulate);
        }
        var resume = ((pump is null) ? null : HostWaits.Serve(reason: in ResumeReason, service: _ => {
            if (Interlocked.Exchange(location1: ref progressed, value: 0) != 0) {
                ResumeOpenStep(charged: HostFramePhase.Wait);
            }

            return null;
        }));

        if (resume is not null) {
            m_simulation!.WakeOnStepProgress(wake: () => {
                _ = Interlocked.Exchange(location1: ref progressed, value: 1);
                resume.Wake();
            });
        }

        var boundary = m_simulation?.TickBoundary;
        boundary?.RegisterPump();
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

                beside = !tickLocked;

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
                frameSteps = ((produce is null) ? 0 : (frameSteps + steps));
                timeline.Mark(phase: HostFramePhase.Simulate);
                timeline.Mark(phase: HostFramePhase.Commands);

                // A step in progress withholds the frame only from a host that steps one tick per frame. Beside the
                // step the frame shows the last tick collected: what the step writes reaches the frame's state only
                // when this thread collects it.
                if ((produce is not null) && (beside || (pump?.StepPending != true))) {
                    var stepsNext = ((pump is not null) && (tickLocked || ((pump.UnbegunTicks + interval) >= stepTicks)));

                    if (pump?.StepPending == true) { timeline.NoteProducedBesideStep(); }
                    holding = produce(new HostIteration(
                        elapsedTicks: (pump?.ElapsedTicks ?? 0UL),
                        hasSimulation: (pump is not null),
                        holding: holding,
                        intervalTicks: interval,
                        loop: this,
                        steps: frameSteps,
                        stepsNext: stepsNext,
                        stepTicks: stepTicks,
                        tickLocked: tickLocked
                    ));
                    frameSteps = 0;
                    // The step the frame ran beside may have finished meanwhile, and the burst goes on behind it.
                    ResumeOpenStep(charged: HostFramePhase.Present, continueBurst: stepTicks);
                    resumeFault?.Throw();
                }
                if (terminal.TryConsumeExit()) {
                    break;
                }
                if (wait is not null) {
                    wait();
                    resumeFault?.Throw();
                } else if (!pacing.IsUnpaced || pump?.StepPending == true) {
                    var period = (frequency / ((long)m_ratePerSecond));

                    nextDeadline += period;
                    var now = time.GetTimestamp();

                    if ((now - nextDeadline) > period) {
                        nextDeadline = now;
                    } else {
                        HostWaits.Pace(deadlineTimestamp: nextDeadline, precisionWaiter: m_waiter, time: time);
                        resumeFault?.Throw();
                        timeline.NoteLate(ticks: Math.Max(val1: 0L, val2: (time.GetTimestamp() - nextDeadline)));
                    }
                    timeline.Mark(phase: HostFramePhase.Wait);
                }
            }

            // A step left executing beside the last iteration is collected before the host tears down. The iterations
            // are over, so the last one's allowance no longer clips the settle's waits.
            if (pump is { StepPending: true }) {
                HostWaits.EndPump();
                using (timeline.FrameWork.Bind()) {
                    _ = pump.Settle();
                }
            }
        } catch (Exception exception) {
            fault = exception;
            throw;
        } finally {
            m_simulation?.WakeOnStepProgress(wake: null);
            resume?.Dispose();
            boundary?.UnregisterPump();
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
