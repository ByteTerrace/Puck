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
/// <item><description>LCD-domain components (the PPU, a cartridge's timed hardware) tick once per whole dot crossed, so
/// their rate is fixed regardless of speed mode.</description></item>
/// </list>
/// The master clock's own sub-cycle phase is the fractional-dot accumulator: a whole-dot boundary is crossed exactly
/// when <see cref="MasterClock.CycleCount"/> increments, which is what tells this driver when the LCD-domain components
/// are due. The driver holds no emulated state of its own beyond the speed flag, so the machine remains fully captured
/// by snapshotting the clock and the components.
/// <para>
/// The component set is FIXED at composition time, so the driver holds each component as a typed field and the
/// per-T-cycle fan-out is direct sealed calls — zero interface dispatch in the hot loop (the ideal plan's §7 mandate).
/// Tick order is the registration order the Contract pins (timer BEFORE serial at an equal timestamp, §3.5), CPU
/// domain first, then LCD domain; the constructor verifies each field's declared <see cref="IClockedComponent.Domain"/>
/// against its hard-coded slot so a component cannot silently change domain without this driver noticing. The only
/// polymorphic slot is the cartridge's timed facet (an MBC3/HuC3 real-time clock); untimed mappers leave it null and
/// pay a predicted-not-taken branch instead of a no-op call.
/// </para>
/// </summary>
public sealed class ComponentClock {
    private readonly MasterClock m_clock;
    private readonly TimerComponent m_timer;
    private readonly Key1Component m_key1;
    private readonly SerialComponent m_serial;
    private readonly ApuComponent m_apu;
    private readonly ApuGeneratorClock m_apuGeneratorClock;
    private readonly AudioOutputComponent m_audioOutput;
    private readonly OamDmaController m_oamDma;
    private readonly HdmaController m_hdma;
    private readonly IClockedComponent? m_cartridgeClock;
    private readonly Ppu m_ppu;

    private bool m_isDoubleSpeed;

    /// <summary>Builds the driver over a clock and the machine's timed components, wiring each into its hard-coded
    /// domain slot and verifying the slot against the component's declared <see cref="IClockedComponent.Domain"/>.</summary>
    /// <param name="clock">The machine's master clock, advanced one CPU T-cycle per <see cref="AdvanceCpuTCycle"/>.</param>
    /// <param name="timer">The divider/timer unit (CPU domain; Contract §3.5 ticks it before <paramref name="serial"/>).</param>
    /// <param name="key1">The KEY1 speed-switch unit (CPU domain).</param>
    /// <param name="serial">The serial unit (CPU domain, after <paramref name="timer"/>).</param>
    /// <param name="apu">The APU frame-sequencer/register unit (CPU domain).</param>
    /// <param name="apuGeneratorClock">The APU generator divider (CPU domain, divides to dots internally).</param>
    /// <param name="audioOutput">The host audio output stage (CPU domain, samples after the APU facets).</param>
    /// <param name="cartridge">The cartridge; its <see cref="IClockedComponent"/> facet (an MBC3/HuC3 real-time
    /// clock) joins the LCD domain when present.</param>
    /// <param name="oamDma">The OAM DMA unit (CPU domain).</param>
    /// <param name="ppu">The PPU (LCD domain).</param>
    /// <param name="hdma">The HDMA unit (CPU domain).</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A component's declared domain does not match its slot.</exception>
    public ComponentClock(
        MasterClock clock,
        TimerComponent timer,
        Key1Component key1,
        SerialComponent serial,
        ApuComponent apu,
        ApuGeneratorClock apuGeneratorClock,
        AudioOutputComponent audioOutput,
        ICartridge cartridge,
        OamDmaController oamDma,
        Ppu ppu,
        HdmaController hdma
    ) {
        ArgumentNullException.ThrowIfNull(argument: clock);
        ArgumentNullException.ThrowIfNull(argument: timer);
        ArgumentNullException.ThrowIfNull(argument: key1);
        ArgumentNullException.ThrowIfNull(argument: serial);
        ArgumentNullException.ThrowIfNull(argument: apu);
        ArgumentNullException.ThrowIfNull(argument: apuGeneratorClock);
        ArgumentNullException.ThrowIfNull(argument: audioOutput);
        ArgumentNullException.ThrowIfNull(argument: cartridge);
        ArgumentNullException.ThrowIfNull(argument: oamDma);
        ArgumentNullException.ThrowIfNull(argument: ppu);
        ArgumentNullException.ThrowIfNull(argument: hdma);

        var cartridgeClock = (cartridge as IClockedComponent);

        VerifyDomain(component: timer, slot: ClockDomain.Cpu);
        VerifyDomain(component: key1, slot: ClockDomain.Cpu);
        VerifyDomain(component: serial, slot: ClockDomain.Cpu);
        VerifyDomain(component: apu, slot: ClockDomain.Cpu);
        VerifyDomain(component: apuGeneratorClock, slot: ClockDomain.Cpu);
        VerifyDomain(component: audioOutput, slot: ClockDomain.Cpu);
        VerifyDomain(component: oamDma, slot: ClockDomain.Cpu);
        VerifyDomain(component: hdma, slot: ClockDomain.Cpu);
        VerifyDomain(component: ppu, slot: ClockDomain.Lcd);

        if (cartridgeClock is not null) {
            VerifyDomain(component: cartridgeClock, slot: ClockDomain.Lcd);
        }

        m_clock = clock;
        m_timer = timer;
        m_key1 = key1;
        m_serial = serial;
        m_apu = apu;
        m_apuGeneratorClock = apuGeneratorClock;
        m_audioOutput = audioOutput;
        m_oamDma = oamDma;
        m_hdma = hdma;
        m_cartridgeClock = cartridgeClock;
        m_ppu = ppu;
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
        TickCpuDomain();
        TickLcdDomain();
    }

    // The double-speed CPU T-cycle: half a dot. At quarter resolution that is two quanta — an exact integer on the
    // fixed-point grid, which is the whole reason the timeline carries sub-dot precision rather than counting whole
    // dots. The CPU-domain components tick every call (twice per dot); the LCD-domain components tick only on the calls
    // that cross a whole-dot boundary.
    private void AdvanceDoubleSpeedTCycle() {
        var dotsBefore = m_clock.CycleCount;

        m_clock.AdvanceTicks(ticks: (m_clock.Resolution.TicksPerCycle >> 1));
        TickCpuDomain();

        for (var dot = dotsBefore; (dot < m_clock.CycleCount); ++dot) {
            TickLcdDomain();
        }
    }

    // Registration order, Contract §3.5: the timer ticks before serial at an equal timestamp.
    private void TickCpuDomain() {
        m_timer.Tick();
        m_key1.Tick();
        m_serial.Tick();
        m_apu.Tick();
        m_apuGeneratorClock.Tick();
        m_audioOutput.Tick();
        m_oamDma.Tick();
        m_hdma.Tick();
    }

    private void TickLcdDomain() {
        m_cartridgeClock?.Tick();
        m_ppu.Tick();
    }

    private static void VerifyDomain(IClockedComponent component, ClockDomain slot) {
        if (component.Domain != slot) {
            throw new InvalidOperationException(
                message: $"{component.GetType().Name} declares domain {component.Domain} but is wired into the {slot} slot."
            );
        }
    }
}
