using System.Text.Json.Serialization;
using Puck.Hosting;

namespace Puck.World;

/// <summary>The <c>timeline</c> section: the world's named presentation clocks, which every time-driven look can read,
/// its sky, its lights and its theme among them. A clock is presentation: it reads the simulation tick or a state row
/// and feeds nothing back into the simulation.</summary>
/// <param name="Clocks">The clocks, each named once.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldTimelineSection(IReadOnlyList<WorldClock>? Clocks = null) {
    /// <summary>Gets the section an unauthored world resolves to: no named clocks.</summary>
    public static WorldTimelineSection Absent { get; } = new();
}
/// <summary>One named presentation clock: a tick clock, whose phase advances with the simulation's engine tick through a
/// period, or a state clock, whose phase is the fractional part of a state row's presented value (an advancing row
/// wraps once per unit).</summary>
/// <param name="Name">The clock's name, unique within the section.</param>
/// <param name="PeriodSeconds">A tick clock's period, in seconds: a whole number of engine ticks
/// (<c>1/50400</c> s). Refused beside <paramref name="State"/>.</param>
/// <param name="SpanSeconds">What one period reads as, in the units a key's time is authored in: a day that passes in
/// twenty minutes and reads as twenty-four hours has a span of <c>24h</c>. Absent reads as the period for a tick clock
/// and as one for a state clock.</param>
/// <param name="StartSeconds">How far into its span a tick clock stands at engine tick zero, in
/// <paramref name="SpanSeconds"/>' units, in <c>[0, span)</c>. Absent is zero. Refused on a state clock.</param>
/// <param name="State">A state clock's Fixed or Int row. Refused beside <paramref name="PeriodSeconds"/>.</param>
/// <param name="Phase">A phase curve keyed on another clock. Refused beside a period or state source.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldClock(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PeriodSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? SpanSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? StartSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? State = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Phase = null
) {
    /// <summary>Gets whether the clock reads a state row rather than the tick.</summary>
    [JsonIgnore]
    public bool IsStateClock => (State is not null);
    /// <summary>Gets what one period reads as: <see cref="SpanSeconds"/>, or the period for a tick clock and one for a
    /// state clock when it is absent.</summary>
    [JsonIgnore]
    public double Span => (SpanSeconds ?? (PeriodSeconds ?? 1d));
}
/// <summary>The pure arithmetic of a <see cref="WorldClock"/>: its period and start in exact engine ticks, and its
/// phase at a presented tick. The validator and every presentation that reads a clock share it, so a clock reads the
/// same everywhere.</summary>
public static class WorldClocks {
    /// <summary>Returns the engine ticks nearest a duration and whether the duration is exactly that many: a clock's
    /// period must be a whole number of engine ticks.</summary>
    /// <param name="seconds">The duration, in seconds.</param>
    /// <param name="ticks">The nearest whole engine ticks, zero when the duration is not finite and positive.</param>
    /// <returns><see langword="true"/> when <paramref name="seconds"/> is finite, positive and a whole number of engine
    /// ticks, to within a millionth of a tick.</returns>
    public static bool TryWholeTicks(double seconds, out ulong ticks) {
        if (!double.IsFinite(d: seconds) || (seconds <= 0d) || ((seconds * EngineTicks.PerSecond) >= ulong.MaxValue)) {
            ticks = 0UL;

            return false;
        }

        var exact = (seconds * EngineTicks.PerSecond);
        var nearest = Math.Round(a: exact);

        ticks = ((ulong)nearest);

        return ((ticks > 0UL) && (Math.Abs(value: (exact - nearest)) <= 1e-6d));
    }
    /// <summary>Returns a tick clock's period in engine ticks.</summary>
    /// <param name="clock">The clock; a tick clock whose period the validator admitted.</param>
    /// <returns>The period, in engine ticks.</returns>
    /// <exception cref="ArgumentException"><paramref name="clock"/> is a state clock or its period is not a whole
    /// number of engine ticks.</exception>
    public static ulong PeriodTicks(WorldClock clock) {
        ArgumentNullException.ThrowIfNull(argument: clock);

        if (!TryWholeTicks(
            seconds: (clock.PeriodSeconds ?? 0d),
            ticks: out var ticks
        )) {
            throw new ArgumentException(
                message: $"Clock '{clock.Name}' has no period of whole engine ticks.",
                paramName: nameof(clock)
            );
        }

        return ticks;
    }
    /// <summary>Returns the engine ticks a tick clock is into its period at engine tick zero: its start as a share of its
    /// span, of its period, rounded to the nearest tick.</summary>
    /// <param name="clock">The tick clock.</param>
    /// <returns>The start, in engine ticks, less than the period.</returns>
    public static ulong StartTicks(WorldClock clock) {
        var period = PeriodTicks(clock: clock);
        var share = ((clock.StartSeconds ?? 0d) / clock.Span);

        return (((ulong)Math.Round(a: (share * period))) % period);
    }
    /// <summary>Returns a tick clock's phase at a presented tick, in <c>[0, 1)</c>.</summary>
    /// <param name="clock">The tick clock.</param>
    /// <param name="tick">The presented tick.</param>
    /// <returns>The phase.</returns>
    public static double Phase(WorldClock clock, PresentedTick tick) => tick.Phase(
        periodTicks: PeriodTicks(clock: clock),
        startTicks: StartTicks(clock: clock)
    );
    /// <summary>Returns a state clock's phase for its row's presented value: the value's fractional part, in
    /// <c>[0, 1)</c>.</summary>
    /// <param name="value">The row's presented value.</param>
    /// <returns>The phase, or zero for a value that is not finite.</returns>
    public static double Phase(double value) => (double.IsFinite(d: value)
        ? (value - Math.Floor(d: value))
        : 0d
    );
}
