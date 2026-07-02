using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// A hardware component the machine advances in lockstep, one of the component's own cycles at a time. The bus master
/// (the CPU) produces the timeline; for every CPU T-cycle it drives, the <see cref="ComponentClock"/> ticks each
/// component the right number of times for that component's <see cref="Domain"/> — so an LCD-domain part sees every
/// whole dot and a CPU-domain part sees every CPU T-cycle. That per-dot resolution is what lets the PPU and APU
/// reproduce sub-instruction, mid-scanline timing exactly.
/// <para>
/// A component owns all of its state as plain fields — its internal counters and its phase within a cycle — never on a
/// call stack or in a captured delegate, which is what keeps the whole machine copyable to the tick and forkable into
/// divergent runs.
/// </para>
/// </summary>
public interface IClockedComponent {
    /// <summary>Gets the clock domain this component is wired to, which fixes how many times it is ticked per dot: a
    /// CPU-domain component advances once per CPU T-cycle (twice per dot under Color double-speed), an LCD-domain
    /// component once per whole dot regardless of speed.</summary>
    ClockDomain Domain { get; }

    /// <summary>Advances the component by exactly one of its own cycles: one dot for an LCD-domain component, one CPU
    /// T-cycle for a CPU-domain component. The component never needs to know the current speed mode — the
    /// <see cref="ComponentClock"/> calls this the correct number of times.</summary>
    void Tick();
}
