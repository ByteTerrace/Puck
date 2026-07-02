using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Timing;

/// <summary>
/// The domain-aware lockstep driver between the <see cref="MasterClock"/> and the machine's timed components. The bus
/// master advances time one CPU T-cycle at a time through <see cref="AdvanceCpuTCycle"/>; each call moves the master
/// clock forward by exactly one CPU T-cycle — a whole dot at normal speed, half a dot under Color double-speed, both
/// integer-exact on the fixed-point grid — and then ticks every component the right number of times for its
/// <see cref="ClockDomain"/>:
/// <list type="bullet">
/// <item><description>CPU-domain components (the divider/timer, serial, OAM DMA) tick once per CPU T-cycle, so they run
/// twice as fast per dot when double-speed is engaged.</description></item>
/// <item><description>LCD-domain components (the PPU, the APU generators) tick once per whole dot crossed, so their rate
/// is fixed regardless of speed mode.</description></item>
/// </list>
/// The master clock's own sub-cycle phase is the fractional-dot accumulator: a whole-dot boundary is crossed exactly
/// when <see cref="MasterClock.CycleCount"/> increments, which is what tells this driver when the LCD-domain components
/// are due. The driver holds no emulated state of its own beyond the speed flag, so the machine remains fully captured
/// by snapshotting the clock and the components.
/// </summary>
public sealed class ComponentClock {
    private readonly MasterClock m_clock;
    private readonly IClockedComponent[] m_cpuComponents;
    private readonly IClockedComponent[] m_lcdComponents;

    private bool m_isDoubleSpeed;

    /// <summary>Builds the driver over a clock and the components resolved for the machine, partitioning them by
    /// <see cref="IClockedComponent.Domain"/> once up front so the per-T-cycle hot path iterates two small arrays.</summary>
    /// <param name="clock">The machine's master clock, advanced one CPU T-cycle per <see cref="AdvanceCpuTCycle"/>.</param>
    /// <param name="components">The timed components, in registration order; each is sorted into its domain.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> or <paramref name="components"/> is
    /// <see langword="null"/>.</exception>
    public ComponentClock(MasterClock clock, IEnumerable<IClockedComponent> components) {
        ArgumentNullException.ThrowIfNull(argument: clock);
        ArgumentNullException.ThrowIfNull(argument: components);

        var cpuComponents = new List<IClockedComponent>();
        var lcdComponents = new List<IClockedComponent>();

        foreach (var component in components) {
            if (component.Domain == ClockDomain.Lcd) {
                lcdComponents.Add(item: component);
            }
            else {
                cpuComponents.Add(item: component);
            }
        }

        m_clock = clock;
        m_cpuComponents = [.. cpuComponents];
        m_lcdComponents = [.. lcdComponents];
    }

    /// <summary>Gets the master clock this driver advances.</summary>
    public MasterClock Clock =>
        m_clock;
    /// <summary>Gets or sets whether the CPU clock is running at Color double-speed, in which case one CPU T-cycle is
    /// half a dot and the CPU-domain components run twice per dot. The KEY1 speed switch drives this seam: a STOP with a
    /// switch armed toggles it, and a snapshot restore re-applies it from the KEY1 unit's own snapshotted state, so the
    /// flag itself is derived rather than serialized here.</summary>
    public bool IsDoubleSpeed {
        get => m_isDoubleSpeed;
        set => m_isDoubleSpeed = value;
    }

    /// <summary>Advances the machine by exactly one CPU T-cycle: moves the master clock forward (a whole dot at normal
    /// speed, half a dot under double-speed), ticks every CPU-domain component once, and ticks every LCD-domain
    /// component once for each whole dot the advance crossed.</summary>
    public void AdvanceCpuTCycle() {
        // Double-speed advances only half a dot per CPU T-cycle, so the whole-dot boundary that makes the LCD-domain
        // components due is crossed on every other call; that bookkeeping lives in the cold path. At normal speed — the
        // overwhelmingly common case — one CPU T-cycle is exactly one whole dot, so the CPU- and LCD-domain components
        // each tick exactly once and there is no boundary to recompute.
        if (m_isDoubleSpeed) {
            AdvanceDoubleSpeedTCycle();

            return;
        }

        m_clock.AdvanceCycles(cycles: 1UL);

        foreach (var component in m_cpuComponents) {
            component.Tick();
        }

        foreach (var component in m_lcdComponents) {
            component.Tick();
        }
    }

    // The double-speed CPU T-cycle: half a dot. At quarter resolution that is two quanta — an exact integer on the
    // fixed-point grid, which is the whole reason the timeline carries sub-dot precision rather than counting whole
    // dots. The CPU-domain components tick every call (twice per dot); the LCD-domain components tick only on the calls
    // that cross a whole-dot boundary.
    private void AdvanceDoubleSpeedTCycle() {
        var dotsBefore = m_clock.CycleCount;

        m_clock.AdvanceTicks(ticks: (m_clock.Resolution.TicksPerCycle >> 1));

        foreach (var component in m_cpuComponents) {
            component.Tick();
        }

        for (var dot = dotsBefore; (dot < m_clock.CycleCount); ++dot) {
            foreach (var component in m_lcdComponents) {
                component.Tick();
            }
        }
    }
}
