using Puck.Commands;
using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>
/// The shared deterministic fixed-step accumulator both boot shapes drive: the windowed run loop
/// (<c>LauncherWindowHostedService</c>) and a headless tick host (<c>HeadlessTickHostedService</c>). It owns the
/// input→simulation contract in one place — the host's console drain → <c>InputRouter.SnapshotForTick</c> →
/// <c>CommandRegistry.ApplySnapshot</c> → <c>IFixedStepSimulation.Step</c>, in that EXACT order, every step — so a
/// boot-shape swap can never reorder it. Draining before every step, not once per host frame, is what makes console
/// work exact against ticks: a line held behind a tick wait runs between the step that released it and the next.
/// </summary>
/// <remarks><para>Wall-clock PACING is the caller's job (a window's present cadence, or a headless waitable-timer
/// loop); this type only turns an already-sampled wall-clock delta into whole simulation steps. Not thread-safe:
/// driven from one pump thread per boot shape, same as the state it wraps.</para>
/// <para><b><see cref="Advance"/> is safe across a change of <c>stepTicks</c> between calls</b> (e.g. a portal
/// crossing into a differently-rated world, per the four-world charter's "no restart" contract) — a design necessity
/// once the simulation rate stops being <c>SimulationRate</c>'s compile-time constant. <see cref="ElapsedTicks"/> and
/// <see cref="AccumulatorTicks"/> are already expressed in engine ticks, the rate-independent unit, so they need no
/// special handling. <see cref="FixedStepContext.Tick"/> is a step ORDINAL — "the zero-based simulation tick being
/// advanced" per its own doc, i.e. how many steps this pump has run, never a time coordinate — so it is tracked as
/// its own monotonic counter rather than re-derived from <c>ElapsedTicks / stepTicks</c>, which silently assumes
/// <c>stepTicks</c> never changed. This makes the pump's OWN bookkeeping correct across a rate change; it does not
/// make a tick number a shared coordinate ACROSS worlds running different rates at once — <c>WorldInstanceHost</c>'s
/// own "Per-instance scheduling" remark now advances each non-boot instance on its OWN authored <c>simulation.rateHz</c>,
/// banking this pump's master-timeline delta (the boot-derived cadence it drives) into each instance's own accumulator
/// and stepping it on its own width, so every instance keeps its own tick ordinal. This pump drives only that master
/// cadence they bank against, never one rate they all share.</para></remarks>
public sealed class FixedStepPump {
    private readonly Action? m_beforeStep;
    private readonly bool m_holdsClock;
    private readonly InputRouter m_inputRouter;
    private readonly Func<bool>? m_mayStep;
    private readonly CommandRegistry m_registry;
    private readonly IFixedStepSimulation m_simulation;

    private ulong m_accumulatorTicks;
    private ulong m_completedStepCount;
    private ulong m_elapsedTicks;

    /// <summary>Initializes the pump over one simulation/router/registry triple and wires the Simulation-phase console
    /// drain — the ONE place either boot shape registers it, so neither can wire it differently.</summary>
    /// <param name="simulation">The fixed-step simulation this pump steps.</param>
    /// <param name="inputRouter">The per-tick command mixer this pump snapshots.</param>
    /// <param name="registry">The command registry Simulation-phase entries apply through.</param>
    /// <param name="captureOriginTicks">The input clock's tick origin at pump construction — the pin newly captured
    /// input is measured against (see <see cref="CaptureOriginTicks"/>).</param>
    /// <param name="beforeStep">Runs immediately before every step, ahead of its input snapshot — the host's console
    /// drain, so a line released by the step just completed (a <c>world.wait</c> deadline) runs before the next step
    /// rather than after the rest of a catch-up burst. <see langword="null"/> drains nothing between steps.</param>
    /// <param name="mayStep">Asked after <paramref name="beforeStep"/> and before every step; <see langword="false"/>
    /// ends this call's stepping and discards the whole steps still due (see <see cref="Advance"/>).
    /// <see langword="null"/> always steps.</param>
    /// <param name="holdsClock">Whether this pump holds its clock for owed frames: before every step it asks
    /// <see cref="IFixedStepSimulation.HoldsClock"/>, and a step the simulation holds is withheld (see
    /// <see cref="Advance"/>). The offscreen host holds; a host paced to a display or to nothing does not.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public FixedStepPump(IFixedStepSimulation simulation, InputRouter inputRouter, CommandRegistry registry, ulong captureOriginTicks, Action? beforeStep = null, Func<bool>? mayStep = null, bool holdsClock = false) {
        ArgumentNullException.ThrowIfNull(argument: simulation);
        ArgumentNullException.ThrowIfNull(argument: inputRouter);
        ArgumentNullException.ThrowIfNull(argument: registry);

        m_simulation = simulation;
        m_inputRouter = inputRouter;
        m_registry = registry;
        m_beforeStep = beforeStep;
        m_holdsClock = holdsClock;
        m_mayStep = mayStep;
        CaptureOriginTicks = captureOriginTicks;

        // The console text door's OWN sink — bound to the Console principal when the router built it (InputRouter's
        // own documented rule), so wiring it here cannot choose what a submitted line acts as.
        m_registry.RouteSimulationTo(sink: m_inputRouter.ConsoleTextSink);
    }

    /// <summary>The sub-step remainder held since the last whole step — the render-side interpolation alpha's
    /// numerator (<c>Puck.Hosting.FrameContext.AccumulatorTicks</c>).</summary>
    public ulong AccumulatorTicks => m_accumulatorTicks;
    /// <summary>The input clock's tick origin newly captured input is measured against — rebased by
    /// <see cref="Advance"/> whenever a runaway wall-clock delta is clamped, so newly captured input stays due now
    /// rather than waiting out simulation time the pump deliberately discarded.</summary>
    public ulong CaptureOriginTicks { get; private set; }
    /// <summary>The exact engine time this pump has advanced the simulation by.</summary>
    public ulong ElapsedTicks => m_elapsedTicks;

    /// <summary>Creates the pump every launcher host loop drives: it drains the console before every step, holds the
    /// first step until piped standard input no longer can hold it, and stops stepping once <c>quit</c> has run.</summary>
    /// <param name="simulation">The registered fixed-step simulation, or <see langword="null"/> when none is.</param>
    /// <param name="inputRouter">The router registered with <paramref name="simulation"/>.</param>
    /// <param name="registry">The command registry.</param>
    /// <param name="inputClock">The input clock whose current reading pins the capture origin.</param>
    /// <param name="textSource">The console text source drained before every step.</param>
    /// <param name="output">The buffered console output flushed after every drain.</param>
    /// <param name="terminal">The terminal whose exit request stops stepping.</param>
    /// <param name="inputBacklog">The standard-input backlog the first step is held on.</param>
    /// <param name="holdsClock">Whether the pump holds its clock for owed frames, which only the offscreen host
    /// does.</param>
    /// <returns>The pump, or <see langword="null"/> when no simulation is registered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/>, <paramref name="inputClock"/>,
    /// <paramref name="textSource"/>, <paramref name="output"/>, <paramref name="terminal"/>, or
    /// <paramref name="inputBacklog"/> is <see langword="null"/>.</exception>
    /// <remarks>The first step waits until the standard-input backlog is released (input ended, failed, is a
    /// terminal, or its writer had written nothing when first read) or the administrative session cannot progress
    /// without a step (<see cref="TextCommandSource.AdministrativeSessionAwaitsStep"/>). Either way every line ahead
    /// of that point has run, however the writer chunked the pipe. The release is sampled before the drain: the reader
    /// releases only after queuing every line it read, so a release seen before the drain is a release whose lines the
    /// drain takes. Once one step has run, the gate never holds again.</remarks>
    public static FixedStepPump? CreateHosted(IFixedStepSimulation? simulation, InputRouter? inputRouter, CommandRegistry registry, IInputClock inputClock, TextCommandSource textSource, BufferedConsoleOutput output, TerminalControl terminal, StandardInputBacklog inputBacklog, bool holdsClock) {
        ArgumentNullException.ThrowIfNull(argument: registry);
        ArgumentNullException.ThrowIfNull(argument: inputClock);
        ArgumentNullException.ThrowIfNull(argument: textSource);
        ArgumentNullException.ThrowIfNull(argument: output);
        ArgumentNullException.ThrowIfNull(argument: terminal);
        ArgumentNullException.ThrowIfNull(argument: inputBacklog);

        if (
            (simulation is null) ||
            (inputRouter is null)
        ) {
            return null;
        }

        var inputReleased = false;
        var started = false;

        return new FixedStepPump(
            beforeStep: () => {
                inputReleased = !inputBacklog.IsHolding;
                textSource.Collect();
                output.Flush();
            },
            captureOriginTicks: inputClock.NowTicks,
            holdsClock: holdsClock,
            inputRouter: inputRouter,
            mayStep: () => {
                started = (started || inputReleased || textSource.AdministrativeSessionAwaitsStep);

                return (started && !terminal.IsExitRequested);
            },
            registry: registry,
            simulation: simulation
        );
    }
    /// <summary>Consumes one sampled wall-clock delta: clamps a runaway frame to <paramref name="maxFrameTicks"/>
    /// (rebasing <see cref="CaptureOriginTicks"/> by the dropped remainder), accumulates it, and runs every whole
    /// <paramref name="stepTicks"/>-sized step now due — drain, snapshot, apply, step, in that order, every time.</summary>
    /// <param name="deltaTicks">The sampled wall-clock delta since the previous call.</param>
    /// <param name="maxFrameTicks">The runaway-frame clamp.</param>
    /// <param name="stepTicks">The step size in engine ticks for steps run by THIS call. May differ from the value
    /// a previous call used (see remarks) — the pump's own bookkeeping stays correct either way.</param>
    /// <returns>The number of whole steps run.</returns>
    /// <remarks><para>The steps of one call are taken one at a time with the console drain before each, so a catch-up
    /// burst is indistinguishable, command for command, from the same steps taken one per call: a session released
    /// by step N runs its next line before step N+1 steps.</para>
    /// <para>A step the host may not take (<c>mayStep</c> answers <see langword="false"/>: its boot input is not yet
    /// read, or it is exiting) ends the call, and the whole steps still due are discarded rather than owed. The
    /// capture pin is rebased by them exactly as for a clamped frame, so nothing bursts when stepping resumes.</para>
    /// <para>A drain may change the simulation's own rate (a <c>world.load</c> of a differently-rated document).
    /// The call then stops without discarding anything, so the host resolves the new step width before the time
    /// already accumulated is spent.</para>
    /// <para>A step after which the simulation <see cref="IFixedStepSimulation.AwaitsFrame"/> also ends the call
    /// without discarding anything, so the host composes the frame that step owes before the next step runs.</para>
    /// <para>A pump that holds its clock asks <see cref="IFixedStepSimulation.HoldsClock"/> before every step, after
    /// the drain and the <c>mayStep</c> gate. A held step ends the call and discards the whole steps still due, with
    /// the capture pin rebased, exactly as a gated one does: the time spent waiting for the frame is never stepped,
    /// so the simulation neither passes the tick the frame must show nor bursts once the frame is served.</para></remarks>
    public int Advance(ulong deltaTicks, ulong maxFrameTicks, ulong stepTicks) {
        if (deltaTicks > maxFrameTicks) {
            // InputClock never clamps, while the simulation intentionally drops excess wall time. Rebase the
            // capture-to-simulation pin by the dropped interval so newly captured input remains due now rather than
            // waiting for simulation time the pump deliberately discarded.
            CaptureOriginTicks += (deltaTicks - maxFrameTicks);
            deltaTicks = maxFrameTicks;
        }

        m_accumulatorTicks += deltaTicks;

        var stepCount = 0;
        var ratePerSecond = m_simulation.RatePerSecond;

        while (m_accumulatorTicks >= stepTicks) {
            m_beforeStep?.Invoke();

            if (m_simulation.RatePerSecond != ratePerSecond) {
                break;
            }

            var heldTicks = ((m_accumulatorTicks / stepTicks) * stepTicks);

            if (
                !(m_mayStep?.Invoke() ?? true) ||
                (m_holdsClock && m_simulation.HoldsClock(withheldTicks: heldTicks))
            ) {
                CaptureOriginTicks += heldTicks;
                m_accumulatorTicks -= heldTicks;

                break;
            }

            m_accumulatorTicks -= stepTicks;

            var tick = m_completedStepCount;

            m_completedStepCount++;
            // The running elapsed-tick total is already rate-independent; never `tick * stepTicks`, which is only
            // valid while `stepTicks` has been constant for every step `tick` counts.
            m_elapsedTicks += stepTicks;

            var stepElapsedTicks = m_elapsedTicks;
            var windowEndTick = (CaptureOriginTicks + stepElapsedTicks);
            var commands = m_inputRouter.SnapshotForTick(
                tick: tick,
                windowEndTick: windowEndTick
            );

            m_registry.ApplySnapshot(snapshot: in commands);

            var fixedStep = new FixedStepContext(
                ElapsedTicks: stepElapsedTicks,
                StepTicks: stepTicks,
                Tick: tick
            );

            m_simulation.Step(
                commands: in commands,
                context: in fixedStep
            );

            stepCount++;

            // A step that owes the host a frame (a capture armed for its tick) ends the burst here, keeping the
            // time still due: the host composes the frame showing this tick, and its next call resumes the catch-up.
            if (m_simulation.AwaitsFrame) {
                break;
            }
        }

        return stepCount;
    }
}
