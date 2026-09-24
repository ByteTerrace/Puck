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
    /// of its own. Read once per pacing-loop iteration, and again after each console drain between steps: a change
    /// ends the current burst, and the next iteration steps at the new width.</summary>
    uint RatePerSecond { get; }
    /// <summary>Whether the step just taken owes the host a composed frame before the next step runs — a capture
    /// armed for that tick, which only a frame produced now can show. Read after every step: <see langword="true"/>
    /// ends the current burst without discarding the time still due, so the host produces its frame and the next
    /// iteration resumes the catch-up. Only the interleaving of frames between steps changes; the steps themselves,
    /// their inputs, and their order are identical either way. A host that produces no frames reads it and simply
    /// takes the rest of its burst on its next iteration.</summary>
    bool AwaitsFrame { get; }

    /// <summary>Asked before every step by a host that holds its clock for owed frames (the offscreen host, whose
    /// frames are its only output): whether to withhold that step because a frame the last step owes has not been
    /// served yet. <see langword="true"/> withholds it: the host spends <paramref name="withheldTicks"/> of its
    /// accumulated time without stepping, produces another frame, and asks again, so no tick past the one the frame
    /// must show runs while that frame is owed. <see langword="false"/> lets the step run. The simulation bounds the
    /// hold itself: once it will wait no longer, it settles what the frame owed (a capture refused by name) and
    /// answers <see langword="false"/>. A host that paces to a display never asks.</summary>
    /// <param name="withheldTicks">The host time, in <see cref="EngineTicks"/>, the host withholds when the answer is
    /// <see langword="true"/>. It is spent, never stepped later, so serving the frame releases no burst.</param>
    /// <returns><see langword="true"/> to withhold the step.</returns>
    bool HoldsClock(ulong withheldTicks);
    /// <summary>Settles every frame the simulation is still owed, because no frame will be produced after this: a
    /// capture no frame served is refused by name and withdrawn from the render chain. A host that produces frames
    /// calls it once as its run ends, after its last produced frame and before it disposes its render root, so what
    /// was owed is decided while the chain that would have served it is still alive.</summary>
    void SettleOwedFrames();
    /// <summary>Advances authoritative state by exactly one fixed tick.</summary>
    /// <param name="context">The exact host-owned tick context.</param>
    /// <param name="commands">The canonical command snapshot already applied to the live command registry.</param>
    void Step(in FixedStepContext context, in CommandSnapshot commands);
}
