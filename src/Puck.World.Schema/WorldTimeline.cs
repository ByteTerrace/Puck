using System.Numerics;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;
using System.Text.Json.Serialization;
using Puck.Hosting;
using Puck.Maths;

namespace Puck.World;

/// <summary>The <c>timeline</c> section: the world's named presentation clocks, which every keyed look can read, its
/// sky, its lights and its theme among them. A clock is presentation: it reads the simulation tick, a state row or
/// where the viewer stands, and feeds nothing back into the simulation.</summary>
/// <param name="Clocks">The clocks, each named once.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldTimelineSection(IReadOnlyList<WorldClock>? Clocks = null) {
    /// <summary>Gets the section an unauthored world resolves to: no named clocks.</summary>
    public static WorldTimelineSection Absent { get; } = new();
}
/// <summary>One named presentation clock: a tick clock, whose phase advances with the simulation's engine tick through a
/// period; a state clock, whose phase is the fractional part of a state row's presented value (an advancing row
/// wraps once per unit); or a view clock, whose phase is how far the viewer stands from a point
/// (<see cref="WorldClockView"/>). A projection carries a state clock as an anchored clock instead, whose phase its
/// recipient predicts from the anchor at the tick it presents (<see cref="WorldClockAnchor"/>); a document never
/// authors one.</summary>
/// <param name="Name">The clock's name, unique within the section.</param>
/// <param name="PeriodSeconds">A tick clock's period, in seconds: a whole number of engine ticks
/// (<c>1/50400</c> s). Refused beside <paramref name="State"/>.</param>
/// <param name="SpanSeconds">What one period reads as, in the units a key's time is authored in: a day that passes in
/// twenty minutes and reads as twenty-four hours has a span of <c>24h</c>. Absent reads as the period for a tick clock,
/// as one for a state clock, and as the distance a view clock covers, so its keys are authored in metres past its
/// near end.</param>
/// <param name="StartSeconds">How far into its span a tick clock stands at engine tick zero, in
/// <paramref name="SpanSeconds"/>' units, in <c>[0, span)</c>. Absent is zero. Refused on a state clock.</param>
/// <param name="State">A state clock's Fixed or Int row. Refused beside <paramref name="PeriodSeconds"/>.</param>
/// <param name="Anchor">An anchored clock's anchor: what a projection carries for a state clock it discloses, in place
/// of the row. Refused in an authored document. An anchored clock carrying none reads no phase, as the state clock it
/// stands for reads none while its row holds no number.</param>
/// <param name="View">A view clock's measure of where the viewer stands. Refused beside <paramref name="State"/> and
/// <paramref name="PeriodSeconds"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldClock(
    [property: DocumentName(WorldNameKind.Clock, WorldNameRole.Declares)] string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PeriodSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? SpanSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? StartSeconds = null,
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? State = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldClockAnchor? Anchor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldClockView? View = null
) {
    /// <summary>Gets whether the clock reads a state row rather than the tick.</summary>
    [JsonIgnore]
    public bool IsStateClock => (State is not null);
    /// <summary>Gets whether the clock reads the tick alone: a period and no state row.</summary>
    [JsonIgnore]
    public bool IsTickClock => ((State is null) && (View is null) && (PeriodSeconds is not null));
    /// <summary>Gets whether the clock reads where the viewer stands rather than the tick or a row.</summary>
    [JsonIgnore]
    public bool IsViewClock => (View is not null);
    /// <summary>Gets whether the clock's phase is read live from its source (<see cref="IWorldClockSource.TryClockPhase"/>)
    /// rather than computed from the presented tick: a state clock or a view clock.</summary>
    [JsonIgnore]
    public bool IsSourceClock => ((State is not null) || (View is not null));
    /// <summary>Gets whether the clock is a projection's anchored clock: neither a period nor a state row, read from the
    /// <see cref="Anchor"/> it carries, if any.</summary>
    [JsonIgnore]
    public bool IsAnchored => ((State is null) && (View is null) && (PeriodSeconds is null));
    /// <summary>Gets what one period reads as: <see cref="SpanSeconds"/>, or when it is absent the period for a tick
    /// clock, the distance covered for a view clock and one for a state clock.</summary>
    [JsonIgnore]
    public double Span => (SpanSeconds ?? (PeriodSeconds ?? (View?.Distance ?? 1d)));
}
/// <summary>What a view clock measures: how far the viewer stands from a point of the world, along an axis for a flat
/// world (a height over its ground) or in any direction for a round one (a height over its sphere). The phase is zero
/// at and inside <paramref name="From"/>, rises evenly to the end of the span at <paramref name="To"/> and holds there
/// beyond it, so a keyed look's last key, eased <see cref="WorldEase.Step"/>, holds past the far end rather than
/// turning back toward the first. One environment resolves a frame, so the clock reads the frame's first view.</summary>
/// <param name="To">The distance from <paramref name="Origin"/> at which the phase reaches the end of the span, in
/// metres; greater than <paramref name="From"/>.</param>
/// <param name="From">The distance at which the phase leaves zero, in metres. Absent is zero.</param>
/// <param name="Origin">The point distances are measured from. Absent is the world's origin.</param>
/// <param name="Axis">The direction distances are measured along, which need not be a unit vector: the viewer's offset
/// from the origin projected on it, so a viewer behind the origin reads zero. Absent measures in every direction, the
/// length of the offset.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldClockView(
    double To,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double From = 0d,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Origin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Axis = null
) {
    /// <summary>Gets the distance the clock covers, <see cref="To"/> less <see cref="From"/>, in metres.</summary>
    [JsonIgnore]
    public double Distance => (To - From);
}
/// <summary>The pure arithmetic of a <see cref="WorldClock"/>: its period and start in exact engine ticks, and its
/// phase at a presented tick. The validator and every presentation that reads a clock share it, so a clock reads the
/// same everywhere.</summary>
public static class WorldClocks {
    /// <summary>Returns the engine ticks nearest a duration and whether the duration is exactly that many: a clock's
    /// period must be a whole number of engine ticks.</summary>
    /// <param name="seconds">The duration, in seconds.</param>
    /// <param name="ticks">The exact whole engine ticks, or the nearest when the duration is not exact; zero when the
    /// duration is not finite and positive.</param>
    /// <returns><see langword="true"/> when <paramref name="seconds"/> is finite, positive and, read as the decimal it
    /// was authored as, exactly a whole number of engine ticks
    /// (<see cref="FixedTickConversion.TryDurationEngineTicksExact"/>).</returns>
    public static bool TryWholeTicks(double seconds, out ulong ticks) {
        if (!double.IsFinite(d: seconds) || (seconds <= 0d) || ((seconds * FixedTickConversion.TicksPerSecond) >= ulong.MaxValue)) {
            ticks = 0UL;

            return false;
        }
        if (
            FixedTickConversion.TryDurationEngineTicksExact(
                seconds: ((decimal)seconds),
                ticks: out ticks
            ) &&
            (ticks > 0UL)
        ) {
            return true;
        }

        ticks = ((ulong)Math.Round(a: (seconds * FixedTickConversion.TicksPerSecond)));

        return false;
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
    /// <summary>Gets the greatest phase a view clock reads, the last double below one: a viewer at or beyond the clock's
    /// far end stands at the end of the span, never back at its start.</summary>
    public static double ViewPhaseCeiling { get; } = Math.BitDecrement(x: 1d);

    /// <summary>Returns a view clock's phase for a viewer's position, in <c>[0, 1)</c>.</summary>
    /// <param name="view">The clock's measure.</param>
    /// <param name="point">The viewer's position, in the world's coordinates.</param>
    /// <returns>The phase: zero at and inside the near distance, <see cref="ViewPhaseCeiling"/> at and beyond the
    /// far one, and zero for a measure or a position that is not finite.</returns>
    public static double Phase(WorldClockView view, Vector3 point) {
        ArgumentNullException.ThrowIfNull(argument: view);

        var offset = (point - (view.Origin?.Value ?? Vector3.Zero));
        var distance = ((view.Axis?.Value is { } axis)
            ? ((double)(Vector3.Dot(vector1: offset, vector2: axis) / axis.Length()))
            : offset.Length());
        var phase = ((distance - view.From) / view.Distance);

        return (double.IsFinite(d: phase)
            ? Math.Clamp(value: phase, min: 0d, max: ViewPhaseCeiling)
            : 0d
        );
    }
    /// <summary>Returns a state clock's phase for its row's presented value: the value's fractional part, in
    /// <c>[0, 1)</c>.</summary>
    /// <param name="value">The row's presented value.</param>
    /// <returns>The phase, or zero for a value that is not finite.</returns>
    public static double Phase(double value) => (double.IsFinite(d: value)
        ? (value - Math.Floor(d: value))
        : 0d
    );
}
