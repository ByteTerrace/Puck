using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;
using Puck.Maths;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The APU's generator clock: hardware clocks the channel generators (the square duty positions, the wave sample
/// fetcher, and the noise LFSR) from the fixed 4 MiHz dot clock, NOT the CPU clock — engaging Color double speed
/// doubles the frame sequencer's DIV source (compensated by the DIV-APU bit moving up) but never the audio pitch.
/// This stateless adapter divides the CPU-domain tick stream down to one <see cref="ApuComponent.TickGenerators"/>
/// call per whole dot, deriving the phase from the master clock so no state needs snapshotting: at normal speed every
/// CPU T-cycle is a whole dot; at double speed the CPU takes two half-dot steps per dot and the generator edge sits on
/// the MID-dot step (the one that leaves the clock off the whole-dot grid) — the hardware-accurate double-speed wave
/// read pairs bracket the wave fetch exactly there, and the whole-dot-boundary alternative leaves every
/// double-speed read one sample behind hardware. It stays in the CPU domain (rather than the LCD domain) so the
/// generators advance between the APU's own frame-sequencer tick and the audio output stage's sampling tick, exactly
/// as they did when the APU was a single component. All mutable state lives (and snapshots) in the APU, so this
/// component is not <see cref="ISnapshotable"/>.
/// </summary>
public sealed class ApuGeneratorClock : IClockedComponent {
    private readonly ApuComponent m_apu;
    private readonly MasterClock m_clock;
    private readonly IKey1 m_key1;

    /// <summary>Creates the generator clock over the APU whose generators it advances.</summary>
    /// <param name="apu">The audio processing unit.</param>
    /// <param name="clock">The machine's master clock, read for the sub-dot phase that marks the generator edge.</param>
    /// <param name="key1">The Color speed-switch unit, read for the current speed.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ApuGeneratorClock(ApuComponent apu, MasterClock clock, IKey1 key1) {
        ArgumentNullException.ThrowIfNull(argument: apu);
        ArgumentNullException.ThrowIfNull(argument: clock);
        ArgumentNullException.ThrowIfNull(argument: key1);

        m_apu = apu;
        m_clock = clock;
        m_key1 = key1;
    }

    /// <inheritdoc/>
    public ClockDomain Domain =>
        ClockDomain.Cpu;

    /// <inheritdoc/>
    public void Tick() {
        // The generator edge: the mid-dot step under double speed, every step otherwise. A speed switch can leave the
        // clock parked half a dot off the grid; whole-dot advances then keep the phase constant, so whichever arm
        // matches keeps firing exactly once per dot.
        if ((m_clock.Now.SubCyclePhase != UFixedQ4816.Zero) || !m_key1.IsDoubleSpeed) {
            m_apu.TickGenerators();
        }
    }
}
