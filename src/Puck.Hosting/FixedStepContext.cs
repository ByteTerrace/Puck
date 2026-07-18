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
    /// <summary>Advances authoritative state by exactly one fixed tick.</summary>
    /// <param name="context">The exact host-owned tick context.</param>
    /// <param name="commands">The canonical command snapshot already applied to the live command registry.</param>
    void Step(in FixedStepContext context, in CommandSnapshot commands);
}
