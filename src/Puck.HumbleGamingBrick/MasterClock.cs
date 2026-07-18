using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.HumbleGamingBrick.Timing;

/// <summary>
/// The single monotonic source of truth for machine time. It holds the current instant as a fixed-point
/// <see cref="Tick"/> whose fundamental tick is a <c>ulong</c>, and it advances only forward and only in whole
/// multiples of its <see cref="Resolution"/>'s quantum, so every instant it can report lands exactly on the timeline
/// grid. One instance is shared per machine; components read <see cref="Now"/> and schedule against it rather than
/// keeping private clocks, which is what makes a run reproducible to the tick.
/// </summary>
public sealed class MasterClock {
    private UFixedQ4816 m_now;

    /// <summary>Creates a clock started at <see cref="Tick.Zero"/> with the given sub-cycle resolution.</summary>
    /// <param name="resolution">The sub-cycle granularity every advance is quantized to.</param>
    public MasterClock(TickResolution resolution) =>
        Resolution = resolution;

    /// <summary>Gets the sub-cycle resolution that quantizes every advance.</summary>
    public TickResolution Resolution { get; }
    /// <summary>Gets the current instant on the master timeline.</summary>
    public Tick Now {
        [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
        get => new(Value: m_now);
    }
    /// <summary>Gets the number of whole T-cycles (LCD dots) elapsed since the clock started.</summary>
    public ulong CycleCount {
        [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
        get => (m_now.Value >> UFixedQ4816.FractionBitCount);
    }
    /// <summary>Gets the number of whole fundamental ticks elapsed since the clock started, at <see cref="Resolution"/>.</summary>
    public ulong TickCount =>
        Now.ToQuanta(resolution: Resolution);

    /// <summary>Advances the clock forward by a duration, which must be a whole multiple of the resolution quantum.</summary>
    /// <param name="delta">The non-negative duration to advance.</param>
    /// <exception cref="ArgumentException"><paramref name="delta"/> is not a whole multiple of the resolution quantum,
    /// which would move the clock off the timeline grid.</exception>
    public void Advance(Tick delta) {
        if ((delta.Value.Value & (Resolution.QuantumRawBits - 1UL)) != 0UL) {
            throw new ArgumentException(
                message: "The advance is not a whole multiple of the resolution quantum.",
                paramName: nameof(delta)
            );
        }

        m_now += delta.Value;
    }
    /// <summary>Advances the clock forward by a whole number of T-cycles (LCD dots).</summary>
    /// <param name="cycles">The number of T-cycles to advance.</param>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public void AdvanceCycles(ulong cycles) =>
        m_now += UFixedQ4816.FromInteger(value: cycles);
    /// <summary>Advances the clock forward by a whole number of fundamental ticks at <see cref="Resolution"/>.</summary>
    /// <param name="ticks">The number of fundamental ticks to advance.</param>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public void AdvanceTicks(ulong ticks) =>
        m_now += UFixedQ4816.FromRawBits(value: unchecked((ticks * Resolution.QuantumRawBits)));
    /// <summary>Sets the clock to an arbitrary instant, bypassing the monotonic-forward guarantee. This is for
    /// restoring a snapshot or forking a machine, where the clock must be repositioned to a captured instant; it is not
    /// part of normal advancement.</summary>
    /// <param name="instant">The instant to reposition the clock to.</param>
    public void ResetTo(Tick instant) =>
        m_now = instant.Value;
}
