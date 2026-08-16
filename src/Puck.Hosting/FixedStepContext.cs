using Puck.Commands;

namespace Puck.Hosting;

/// <summary>One exact fixed simulation step dispatched by the host. The launcher is the sole owner of the wall-clock
/// accumulator; consumers receive integer engine ticks and never reconstruct steps from floating-point seconds.</summary>
/// <param name="Tick">The zero-based simulation tick being advanced.</param>
/// <param name="ElapsedTicks">The exact simulation time after this step completes.</param>
/// <param name="StepTicks">The exact duration of one step in <see cref="EngineTicks"/>.</param>
public readonly record struct FixedStepContext(ulong Tick, ulong ElapsedTicks, ulong StepTicks);
/// <summary>The optional deterministic simulation seam driven by a Puck host. For each due fixed tick the launcher
/// builds one <see cref="CommandSnapshot"/>, applies it to the command registry, then calls <see cref="Step"/> once.</summary>
public interface IFixedStepSimulation {
    /// <summary>The fixed rate, in Hz, this simulation steps at — MUST divide <see cref="EngineTicks.PerSecond"/>
    /// exactly (<see cref="EngineTicks.PerRate"/> is how the host turns it into a step width). <c>Puck.Hosting</c> is
    /// domain-agnostic and owns no notion of "the" simulation rate — a genre-specific host (e.g. a loaded world
    /// document) is what actually declares one, so the launcher reads it here rather than assuming a fixed constant
    /// of its own. Read once per pacing-loop iteration; a mid-run change is not a contract this seam makes today.</summary>
    uint RatePerSecond { get; }

    /// <summary>Advances authoritative state by exactly one fixed tick.</summary>
    /// <param name="context">The exact host-owned tick context.</param>
    /// <param name="commands">The canonical command snapshot already applied to the live command registry.</param>
    void Step(in FixedStepContext context, in CommandSnapshot commands);
}
