using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>Per-owner bounded input retention and timing policy.</summary>
/// <param name="RetentionSeconds">How long an admitted event remains eligible.</param>
/// <param name="EventCapacity">Ring capacity, allocated when the owner is admitted.</param>
/// <param name="MaximumEventsPerStep">Atomic per-step ingress bound, including derived crossings.</param>
/// <param name="MaximumLatenessSeconds">Capture horizon behind the next open interval.</param>
/// <param name="CalibrationLimitSeconds">Absolute calibration bound.</param>
/// <param name="Judgement">Which recorded time rules use.</param>
/// <param name="Crossings">Named threshold/hysteresis predicates over ordered samples.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInputHistory(
    decimal RetentionSeconds = 2,
    int EventCapacity = 256,
    int MaximumEventsPerStep = 128,
    decimal MaximumLatenessSeconds = 0,
    decimal CalibrationLimitSeconds = 0,
    WorldInputJudgement Judgement = WorldInputJudgement.Capture,
    IReadOnlyList<WorldInputCrossing>? Crossings = null
) {
    /// <summary>Refuses unsafe retention/work bounds before allocating any owner state.</summary>
    public void Validate() {
        if (EventCapacity is < 1 or > ushort.MaxValue || MaximumEventsPerStep is < 1 or > ushort.MaxValue ||
            MaximumEventsPerStep > EventCapacity || RetentionSeconds <= 0 || MaximumLatenessSeconds < 0 ||
            MaximumLatenessSeconds > RetentionSeconds || CalibrationLimitSeconds < 0 || !Enum.IsDefined(Judgement) ||
            (Crossings?.Count ?? 0) > WorldInputCatalog.MaximumLogicalChannels) {
            throw new ArgumentException("inputHistory has invalid event, crossing, retention, lateness or calibration bounds");
        }
        _ = FixedQ4816.Parse(RetentionSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        _ = FixedQ4816.Parse(CalibrationLimitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        if (FixedTickConversion.DurationEngineTicks(MaximumLatenessSeconds) > 999_998) {
            throw new ArgumentException("inputHistory lateness requires more than 1,000,000 retained engine intervals");
        }
    }
}

/// <summary>The recorded clock coordinate used for judgement.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldInputJudgement>))]
public enum WorldInputJudgement : byte {
    /// <summary>Captured time, including the owner's admitted calibration.</summary>
    Capture,
    /// <summary>Processing interval start.</summary>
    Admission,
}

/// <summary>A hysteretic semantic crossing, derived only from accepted ordered axis samples.</summary>
/// <param name="Name">Pattern-visible crossing name.</param>
/// <param name="Channel">Declared normalized axis.</param>
/// <param name="Press">Threshold which enters the predicate.</param>
/// <param name="Release">Threshold which leaves the predicate.</param>
/// <param name="Direction">One for increasing and minus one for decreasing values.</param>
/// <param name="FacingState">Optional authoritative body counter whose sign orients this axis at admission.
/// Zero has no facing and cannot enter the crossing. History retains the resolved crossing, not a later counter read.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInputCrossing(string Name, string Channel, decimal Press, decimal Release, int Direction = 1,
    string? FacingState = null);

/// <summary>A captured gameplay interval. Rate changes retain these exact engine coordinates.</summary>
/// <param name="Tick">Assigned simulation tick.</param>
/// <param name="EngineStart">Exact interval start on the engine clock.</param>
/// <param name="EngineDuration">Exact positive interval width.</param>
/// <param name="Music">The existing music clock at interval start, if declared.</param>
public readonly record struct WorldInputInterval(ulong Tick, ulong EngineStart, ulong EngineDuration, WorldInputMusicSegment? Music = null) {
    /// <summary>Folds the exact interval and its historical music coordinates.</summary>
    public void AppendStateHash(ref Fnv1aHash hash) {
        hash.Add(Tick); hash.Add(EngineStart); hash.Add(EngineDuration); hash.Add((byte)(Music.HasValue ? 1 : 0));
        if (Music is { } music) { hash.Add(music.ElapsedTicks); hash.Add(music.TicksPerBeat); hash.Add(music.Segment); }
    }
    /// <summary>Tests the half-open interval before admitting a fixed sub-tick timestamp.</summary>
    public bool Contains(FixedQ4816 offset) => offset.Value >= 0 &&
        (UInt128)(ulong)offset.Value * FixedTickConversion.TicksPerSecond < (UInt128)EngineDuration << FixedQ4816.FractionBitCount;

    /// <summary>Combines the exact engine origin, offset and calibration in one widened, ties-to-even rounding.</summary>
    public FixedQ4816 Time(FixedQ4816 offset, FixedQ4816 calibration = default) {
        var numerator = ((Int128)EngineStart << FixedQ4816.FractionBitCount) +
            ((Int128)offset.Value + calibration.Value) * FixedTickConversion.TicksPerSecond;
        var negative = numerator < 0;
        var magnitude = (UInt128)(negative ? -numerator : numerator);
        var denominator = (UInt128)FixedTickConversion.TicksPerSecond;
        var quotient = magnitude / denominator;
        var remainder = magnitude % denominator;
        var rounded = FixedPointRounding.RoundToNearestTiesToEven(quotient, remainder, denominator - remainder);
        var signed = negative ? -(Int128)rounded : (Int128)rounded;
        return FixedQ4816.FromRawBits(checked((long)signed));
    }

    /// <summary>Quantizes a rational captured offset once, carrying a rounded endpoint into the next interval.</summary>
    public (ulong Tick, FixedQ4816 Offset) Quantize(ulong numerator, ulong denominator) {
        ArgumentOutOfRangeException.ThrowIfZero(denominator);
        if ((UInt128)numerator * FixedTickConversion.TicksPerSecond >= (UInt128)EngineDuration * denominator) {
            throw new ArgumentOutOfRangeException(nameof(numerator), "Capture lies outside its assigned interval.");
        }
        var raw = (UInt128)numerator << FixedQ4816.FractionBitCount;
        var quotient = raw / denominator;
        var remainder = raw % denominator;
        var offset = FixedQ4816.FromRawBits(checked((long)FixedPointRounding.RoundToNearestTiesToEven(quotient, remainder, (UInt128)denominator - remainder)));
        if (Contains(offset)) { return (Tick, offset); }
        // This entry point quantizes an observation already assigned to this exact
        // interval; only rounding onto its endpoint is allowed to carry.
        return (checked(Tick + 1), FixedQ4816.Zero);
    }
}

/// <summary>The existing music clock at capture-interval start, preserved through tempo/segment changes.</summary>
public readonly record struct WorldInputMusicSegment(ulong ElapsedTicks, long TicksPerBeat, string Segment) {
    /// <summary>Signed nearest-beat error in Q48.16 seconds, with one widened ties-to-even rounding.</summary>
    public FixedQ4816 PhaseError(FixedQ4816 offset, FixedQ4816 calibration) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TicksPerBeat);
        var period = (Int128)TicksPerBeat << FixedQ4816.FractionBitCount;
        var position = ((Int128)ElapsedTicks << FixedQ4816.FractionBitCount) +
            ((Int128)offset.Value + calibration.Value) * FixedTickConversion.TicksPerSecond;
        var phase = position % period;
        if (phase < 0) { phase += period; }
        if (phase > period / 2) { phase -= period; }
        var magnitude = (UInt128)(phase < 0 ? -phase : phase);
        var divisor = (UInt128)FixedTickConversion.TicksPerSecond;
        var remainder = magnitude % divisor;
        var raw = FixedPointRounding.RoundToNearestTiesToEven(magnitude / divisor, remainder, divisor - remainder);
        return FixedQ4816.FromRawBits(checked((long)(phase < 0 ? -(Int128)raw : (Int128)raw)));
    }
}

/// <summary>Semantic wire events. Crossings and cancellation outcomes are authority-derived history records.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldInputEventKind>))]
public enum WorldInputEventKind : byte {
    /// <summary>A normalized button's real press.</summary>
    Press,
    /// <summary>A normalized button's real release.</summary>
    Release,
    /// <summary>An ordered normalized axis sample.</summary>
    AxisSample,
    /// <summary>One Unicode scalar from typing, paste or committed IME text.</summary>
    Text,
    /// <summary>An authority-derived named threshold crossing.</summary>
    AxisCrossing,
    /// <summary>A reset ends a hold without a gameplay release.</summary>
    Cancel,
}

/// <summary>The shared bounded event leaf; names resolve through the captured logical catalog.</summary>
/// <param name="Channel">Logical channel id.</param>
/// <param name="Kind">Semantic event kind.</param>
/// <param name="RawValue">A Q48.16 sample, exact button zero/one, or a Unicode scalar.</param>
/// <param name="CaptureTick">Capture interval identity.</param>
/// <param name="Offset">Q48.16 seconds within that interval.</param>
/// <param name="Sequence">Monotonic owner sequence within the epoch.</param>
/// <param name="Activation">Shared identity of sibling outputs of one physical activation.</param>
public readonly record struct WorldInputEvent(ushort Channel, WorldInputEventKind Kind, long RawValue,
    ulong CaptureTick, FixedQ4816 Offset, uint Sequence, ulong Activation);

/// <summary>An admitted event with immutable capture and processing coordinates.</summary>
/// <param name="Id">Owner-local identity, never reused across epochs.</param>
/// <param name="Epoch">The input epoch which admitted this record.</param>
/// <param name="Event">The original semantic event.</param>
/// <param name="Capture">Historical interval retained with this event.</param>
/// <param name="Admission">Interval in which simulation processed it.</param>
/// <param name="HoldPress">Original press identity for a release or continuing hold.</param>
/// <param name="Crossing">Named crossing ordinal, or -1.</param>
/// <param name="Calibration">Calibration captured at admission.</param>
public readonly record struct WorldInputRecord(ulong Id, ulong Epoch, WorldInputEvent Event,
    WorldInputInterval Capture, WorldInputInterval Admission, ulong HoldPress = 0, int Crossing = -1,
    FixedQ4816 Calibration = default) {
    /// <summary>Whether processing happened after the capture interval.</summary>
    public bool Late => Capture.Tick < Admission.Tick;
    /// <summary>The selected deterministic judgement time.</summary>
    public FixedQ4816 Time(WorldInputJudgement policy) => policy == WorldInputJudgement.Capture
        ? Capture.Time(Event.Offset, Calibration) : Admission.Time(default);
}
