using System.Diagnostics;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>Optional per-step timing accumulators, filled only while [frame-timing] is armed (see
/// <c>Puck.Hosting.GpuTimingControl</c>). A caller that does not care about the sub-bucket breakdown passes
/// <see langword="null"/> to <see cref="FixedStepPump.Advance"/> and pays nothing for it.</summary>
public sealed class FixedStepTimingAccumulator {
    /// <summary>Total ticks spent inside every <c>CommandRegistry.ApplySnapshot</c> call this iteration.</summary>
    public long CommandApplyTicks;
    /// <summary>Total ticks spent inside every <c>InputRouter.SnapshotForTick</c> call this iteration.</summary>
    public long InputSnapshotTicks;
    /// <summary>Total ticks spent inside every <c>IFixedStepSimulation.Step</c> call this iteration.</summary>
    public long SimulationStepTicks;
}
/// <summary>
/// The shared deterministic fixed-step accumulator both boot shapes drive: the windowed run loop
/// (<c>LauncherWindowHostedService</c>) and a headless tick host (<c>HeadlessTickHostedService</c>). It owns the
/// input→simulation contract in one place — <c>InputRouter.SnapshotForTick</c> → <c>CommandRegistry.ApplySnapshot</c>
/// → <c>IFixedStepSimulation.Step</c>, in that EXACT order, every step — so a boot-shape swap can never reorder it (the
/// headless verification runner's sabotage case swaps the order deliberately to prove the order is load-bearing).
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
    private readonly InputRouter m_inputRouter;
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
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public FixedStepPump(IFixedStepSimulation simulation, InputRouter inputRouter, CommandRegistry registry, ulong captureOriginTicks) {
        ArgumentNullException.ThrowIfNull(argument: simulation);
        ArgumentNullException.ThrowIfNull(argument: inputRouter);
        ArgumentNullException.ThrowIfNull(argument: registry);

        m_simulation = simulation;
        m_inputRouter = inputRouter;
        m_registry = registry;
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

    /// <summary>Consumes one sampled wall-clock delta: clamps a runaway frame to <paramref name="maxFrameTicks"/>
    /// (rebasing <see cref="CaptureOriginTicks"/> by the dropped remainder), accumulates it, and runs every whole
    /// <paramref name="stepTicks"/>-sized step now due — snapshot, apply, step, in that order, every time.</summary>
    /// <param name="deltaTicks">The sampled wall-clock delta since the previous call.</param>
    /// <param name="maxFrameTicks">The runaway-frame clamp.</param>
    /// <param name="stepTicks">The step size in engine ticks for steps run by THIS call. May differ from the value
    /// a previous call used (see remarks) — the pump's own bookkeeping stays correct either way.</param>
    /// <param name="timing">An optional accumulator for the [frame-timing] sub-bucket breakdown; <see langword="null"/>
    /// skips the per-phase <see cref="Stopwatch"/> sampling entirely.</param>
    /// <returns>The number of whole steps run.</returns>
    public int Advance(ulong deltaTicks, ulong maxFrameTicks, ulong stepTicks, FixedStepTimingAccumulator? timing = null) {
        if (deltaTicks > maxFrameTicks) {
            // InputClock never clamps, while the simulation intentionally drops excess wall time. Rebase the
            // capture-to-simulation pin by the dropped interval so newly captured input remains due now rather than
            // waiting for simulation time the pump deliberately discarded.
            CaptureOriginTicks += (deltaTicks - maxFrameTicks);
            deltaTicks = maxFrameTicks;
        }

        m_accumulatorTicks += deltaTicks;

        var consumedTicks = ((m_accumulatorTicks / stepTicks) * stepTicks);

        m_accumulatorTicks -= consumedTicks;

        var previousElapsedTicks = m_elapsedTicks;

        m_elapsedTicks += consumedTicks;

        var stepCount = (consumedTicks / stepTicks);
        var firstTick = m_completedStepCount;

        m_completedStepCount += stepCount;

        for (var stepIndex = 0UL; (stepIndex < stepCount); stepIndex++) {
            var tick = (firstTick + stepIndex);
            // Derived from the running elapsed-tick total (already rate-independent), never from `tick * stepTicks`
            // — that product is only valid while `stepTicks` has been constant for every step `tick` counts, which a
            // step-size change between Advance calls breaks.
            var stepElapsedTicks = (previousElapsedTicks + ((stepIndex + 1UL) * stepTicks));
            var windowEndTick = (CaptureOriginTicks + stepElapsedTicks);
            var snapshotStart = ((timing is not null)
                ? Stopwatch.GetTimestamp()
                : 0L
            );
            var commands = m_inputRouter.SnapshotForTick(
                tick: tick,
                windowEndTick: windowEndTick
            );

            if (timing is not null) {
                timing.InputSnapshotTicks += (Stopwatch.GetTimestamp() - snapshotStart);
            }

            var applyStart = ((timing is not null)
                ? Stopwatch.GetTimestamp()
                : 0L
            );

            m_registry.ApplySnapshot(snapshot: in commands);

            if (timing is not null) {
                timing.CommandApplyTicks += (Stopwatch.GetTimestamp() - applyStart);
            }

            var fixedStep = new FixedStepContext(
                ElapsedTicks: stepElapsedTicks,
                StepTicks: stepTicks,
                Tick: tick
            );
            var stepStart = ((timing is not null)
                ? Stopwatch.GetTimestamp()
                : 0L
            );

            m_simulation.Step(
                commands: in commands,
                context: in fixedStep
            );

            if (timing is not null) {
                timing.SimulationStepTicks += (Stopwatch.GetTimestamp() - stepStart);
            }
        }

        return ((int)stepCount);
    }
}
