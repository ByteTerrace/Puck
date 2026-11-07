using Puck.Maths;

namespace Puck.State;

/// <summary>One live fact off a rule operand: a raw value in a cell encoding, positive infinity (<see cref="FaultCase.Forever"/>) for a channel whose magnitude can exceed every number,
/// no cell at all (<see cref="FaultCase.Absent"/>) for an operand whose dynamic key named none — an ordered zone's
/// endpoint read while the zone was empty — or a fault (<see cref="FaultCase.Faulted"/>) for an operand whose address
/// computation failed.</summary>
/// <remarks>Infinity participates in comparisons through
/// <see cref="ExpressionComparisons.Holds(ExpressionOp, FixedQ4816, bool, FixedQ4816, bool)"/>; an absent fact holds no
/// comparison at all (neither equal nor not-equal to anything), faults an expression, and stops a copy from firing,
/// each refusing by name and pointing at <see cref="ExpressionOp.IsAbsent"/> and <see cref="ExpressionOp.Coalesce"/>,
/// the operations that answer an absence. A faulted fact is read as an absence by a consumer that answers only
/// absences, and as arithmetic failure by one that evaluates. Neither is ever encoded as a numeric stand-in.</remarks>
public readonly struct Fact {
    /// <summary>Initializes a new instance of the <see cref="Fact"/> struct.</summary>
    /// <param name="outcome">What the operand read answered.</param>
    /// <param name="kind">The encoding a finite reading of the same operand carries.</param>
    public Fact(Outcome outcome, CellKind kind) {
        Outcome = outcome;
        Kind = kind;
    }

    /// <summary>Gets what the operand read answered.</summary>
    public Outcome Outcome { get; }
    /// <summary>Gets the encoding a finite reading carries.</summary>
    public CellKind Kind { get; }

    /// <summary>Creates the no-cell fact in a cell encoding.</summary>
    /// <param name="kind">The encoding a finite reading of the same operand would carry.</param>
    public static Fact Absent(CellKind kind) => new(
        kind: kind,
        outcome: Outcome.Absent
    );
    /// <summary>Creates the fact of an operand whose address computation faulted.</summary>
    /// <param name="kind">The encoding a finite reading of the same operand would carry.</param>
    public static Fact Faulted(CellKind kind) => new(
        kind: kind,
        outcome: Outcome.Faulted(why: ExpressionFault.Domain)
    );
    /// <summary>Creates a finite fixed-point fact.</summary>
    /// <param name="value">The value.</param>
    public static Fact Finite(FixedQ4816 value) => new(
        kind: CellKind.Fixed,
        outcome: Outcome.Finite(raw: value.Value)
    );
    /// <summary>Creates a finite fact in a cell encoding.</summary>
    /// <param name="value">The raw value.</param>
    /// <param name="kind">The encoding.</param>
    public static Fact Finite(long value, CellKind kind) => new(
        kind: kind,
        outcome: Outcome.Finite(raw: value)
    );
    /// <summary>Creates the positive-infinity fact in a cell encoding.</summary>
    /// <param name="kind">The encoding a finite reading of the same channel would carry.</param>
    public static Fact Forever(CellKind kind) => new(
        kind: kind,
        outcome: Outcome.Forever
    );
    /// <summary>Returns a fixed-point value's raw bits in a cell encoding: the bits themselves for
    /// <see cref="CellKind.Fixed"/>, nonzero-as-1 for <see cref="CellKind.Bool"/>, the whole part otherwise.</summary>
    /// <param name="value">The fixed-point value.</param>
    /// <param name="kind">The destination encoding.</param>
    public static long RawOf(FixedQ4816 value, CellKind kind) => kind switch {
        CellKind.Fixed => value.Value,
        CellKind.Bool => ((value.Value == 0L)
        ? 0L
        : 1L),
        _ => (value.Value >> FixedQ4816.FractionBitCount),
    };
    /// <summary>Returns the fact's raw value in a destination encoding, or zero when the fact is not finite.
    /// Compile-time kind matching proves the source and destination encodings agree, so the raw value is carried
    /// through directly and a full-width integer is never narrowed through Q48.16; a <see cref="CellKind.Bool"/>
    /// destination reads nonzero as 1.</summary>
    /// <param name="kind">The destination encoding.</param>
    public long ToRaw(CellKind kind) {
        Outcome.TryFinite(raw: out var raw);

        return (kind switch {
            CellKind.Bool => ((raw != 0L)
            ? 1L
            : 0L),
            _ => raw,
        });
    }
}
