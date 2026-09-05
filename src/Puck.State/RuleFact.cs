using Puck.Maths;

namespace Puck.State;

/// <summary>One live fact off a rule operand: a raw value in a cell encoding, or positive infinity
/// (<see cref="IsForever"/>) for a channel whose magnitude can exceed every number. Infinity participates in
/// comparisons through <see cref="ActionStateComparisons.Holds(ActionStateComparison, FixedQ4816, bool, FixedQ4816, bool)"/>
/// and is never encoded as a numeric stand-in.</summary>
/// <param name="Value">The raw value in <paramref name="Kind"/>'s encoding; ignored when <paramref name="IsForever"/>.</param>
/// <param name="Kind">The encoding <paramref name="Value"/> carries.</param>
/// <param name="IsForever">Whether the fact is positive infinity.</param>
public readonly record struct RuleFact(long Value, CellKind Kind, bool IsForever) {
    /// <summary>Creates a finite fixed-point fact.</summary>
    /// <param name="value">The value.</param>
    public static RuleFact Finite(FixedQ4816 value) => new(Value: value.Value, Kind: CellKind.Fixed, IsForever: false);
    /// <summary>Creates a finite fact in a cell encoding.</summary>
    /// <param name="value">The raw value.</param>
    /// <param name="kind">The encoding.</param>
    public static RuleFact Finite(long value, CellKind kind) => new(Value: value, Kind: kind, IsForever: false);
    /// <summary>Creates the positive-infinity fact in a cell encoding.</summary>
    /// <param name="kind">The encoding a finite reading of the same channel would carry.</param>
    public static RuleFact Forever(CellKind kind) => new(Value: 0L, Kind: kind, IsForever: true);
    /// <summary>Returns the fact's raw value in a destination encoding. Compile-time kind matching proves the source
    /// and destination encodings agree, so the raw value is carried through directly and a full-width integer is
    /// never narrowed through Q48.16; a <see cref="CellKind.Bool"/> destination reads nonzero as 1.</summary>
    /// <param name="kind">The destination encoding.</param>
    public long ToRaw(CellKind kind) => kind switch {
        CellKind.Bool => ((Value != 0L) ? 1L : 0L),
        _ => Value,
    };
    /// <summary>Returns a fixed-point value's raw bits in a cell encoding: the bits themselves for
    /// <see cref="CellKind.Fixed"/>, nonzero-as-1 for <see cref="CellKind.Bool"/>, the whole part otherwise.</summary>
    /// <param name="value">The fixed-point value.</param>
    /// <param name="kind">The destination encoding.</param>
    public static long RawOf(FixedQ4816 value, CellKind kind) => kind switch {
        CellKind.Fixed => value.Value,
        CellKind.Bool => ((value.Value == 0L) ? 0L : 1L),
        _ => (value.Value >> FixedQ4816.FractionBitCount),
    };
}
