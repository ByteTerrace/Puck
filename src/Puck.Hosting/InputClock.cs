using System.Diagnostics;

using Puck.Commands;

namespace Puck.Hosting;

/// <summary>
/// The process-wide monotonic capture clock, in <see cref="EngineTicks"/>, shared by every input backend so
/// all <see cref="InputSignal.CaptureTick"/> stamps share one base. Stopwatch-derived; measures real arrival
/// time (never clamped or stepped), unlike the fixed-step simulation clock (<see cref="TickClock"/>).
/// </summary>
/// <remarks>
/// <see cref="NowTicks"/> is a pure function of elapsed Stopwatch ticks, so it is inherently monotonic and
/// lock-free — safe to read from device I/O threads. The conversion is the exact floor of
/// <c>elapsed × <see cref="EngineTicks.PerSecond"/> ÷ frequency</c>, computed without overflow by splitting
/// whole seconds from the sub-second remainder.
/// </remarks>
public sealed class InputClock : IInputClock {
    private readonly long m_originTimestamp;
    private readonly ulong m_frequency;

    private InputClock(long originTimestamp, ulong frequency) {
        m_frequency = frequency;
        m_originTimestamp = originTimestamp;
    }

    /// <summary>Starts a clock whose origin is the current instant.</summary>
    public static InputClock Start() {
        return new InputClock(
            frequency: (ulong)Stopwatch.Frequency,
            originTimestamp: Stopwatch.GetTimestamp()
        );
    }

    /// <summary>The Stopwatch frequency this clock scales from, exposed so a backend can build an <see cref="OsTimeCorrelator"/>.</summary>
    public ulong Frequency => m_frequency;

    /// <inheritdoc/>
    public ulong NowTicks {
        get {
            var elapsed = unchecked((ulong)(Stopwatch.GetTimestamp() - m_originTimestamp));

            // floor(elapsed × PerSecond ÷ frequency), overflow-safe and exact: split whole seconds from the
            // sub-second remainder so neither product overflows for any realistic session length.
            var whole = (elapsed / m_frequency);
            var fraction = (elapsed % m_frequency);

            return ((whole * EngineTicks.PerSecond) + ((fraction * EngineTicks.PerSecond) / m_frequency));
        }
    }
}
