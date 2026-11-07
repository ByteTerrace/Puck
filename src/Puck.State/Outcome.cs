namespace Puck.State;

/// <summary>Which case one evaluation answered. The zero value is <see cref="Faulted"/>, so a defaulted answer refuses
/// rather than reads as a number.</summary>
public enum FaultCase : byte {
    /// <summary>The evaluation faulted (overflow, a divide by zero, a value with no number, or a key whose computation
    /// failed). A fault absorbs: anything consuming it is faulted too.</summary>
    Faulted = 0,
    /// <summary>The evaluation answered a number (for an address: it named a cell).</summary>
    Finite,
    /// <summary>The evaluation answered an absence, a value each consumer answers where it sits (for an address: it
    /// named nothing).</summary>
    Absent,
    /// <summary>The evaluation answered positive infinity: a magnitude beyond every number.</summary>
    Forever,
}
/// <summary>What one evaluation answered: a raw number, an absence, positive infinity, or a fault, as cases of one
/// value rather than a flag beside it.</summary>
/// <remarks>The raw number is reachable only through <see cref="TryFinite"/>, so no caller can read a number from an
/// answer that has none. <c>default(Outcome)</c> is <see cref="FaultCase.Faulted"/> with
/// <see cref="ExpressionFault.Domain"/>.</remarks>
public readonly struct Outcome : IEquatable<Outcome> {
    /// <summary>The absence: the answer of an operand that named no cell.</summary>
    public static readonly Outcome Absent = new(
        faultCase: FaultCase.Absent,
        fault: ExpressionFault.None,
        raw: 0L
    );
    /// <summary>Positive infinity.</summary>
    public static readonly Outcome Forever = new(
        faultCase: FaultCase.Forever,
        fault: ExpressionFault.None,
        raw: 0L
    );

    private readonly long m_raw;
    private readonly FaultCase m_case;
    private readonly ExpressionFault m_fault;

    private Outcome(FaultCase faultCase, ExpressionFault fault, long raw) {
        m_case = faultCase;
        m_fault = fault;
        m_raw = raw;
    }

    /// <summary>Gets which case the evaluation answered.</summary>
    public FaultCase Case => m_case;
    /// <summary>Gets why the evaluation faulted: <see cref="ExpressionFault.Domain"/> or
    /// <see cref="ExpressionFault.Forever"/> when <see cref="Case"/> is <see cref="FaultCase.Faulted"/>, and
    /// <see cref="ExpressionFault.None"/> otherwise.</summary>
    public ExpressionFault Fault => ((m_case == FaultCase.Faulted)
        ? ((m_fault == ExpressionFault.None)
            ? ExpressionFault.Domain
            : m_fault)
        : ExpressionFault.None);

    /// <summary>Creates a faulted answer.</summary>
    /// <param name="why">The fault: <see cref="ExpressionFault.Domain"/> or <see cref="ExpressionFault.Forever"/>.</param>
    public static Outcome Faulted(ExpressionFault why) => new(
        faultCase: FaultCase.Faulted,
        fault: why,
        raw: 0L
    );
    /// <summary>Creates a finite answer.</summary>
    /// <param name="raw">The raw value in the encoding of whatever carries the answer.</param>
    public static Outcome Finite(long raw) => new(
        faultCase: FaultCase.Finite,
        fault: ExpressionFault.None,
        raw: raw
    );

    /// <inheritdoc/>
    public bool Equals(Outcome other) => ((m_case == other.m_case) && (Fault == other.Fault) && (m_raw == other.m_raw));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is Outcome other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(m_case, Fault, m_raw);
    /// <inheritdoc/>
    public override string ToString() => (m_case switch {
        FaultCase.Finite => $"finite({m_raw})",
        FaultCase.Faulted => $"faulted({Fault})",
        FaultCase.Absent => "absent",
        _ => "forever",
    });
    /// <summary>Reads the number when the answer is finite.</summary>
    /// <param name="raw">The raw value, or zero when the answer is not finite.</param>
    /// <returns><see langword="true"/> when the answer is finite.</returns>
    public bool TryFinite(out long raw) {
        raw = ((m_case == FaultCase.Finite)
            ? m_raw
            : 0L);

        return (m_case == FaultCase.Finite);
    }

    /// <summary>Compares two answers.</summary>
    public static bool operator ==(Outcome left, Outcome right) => left.Equals(other: right);
    /// <summary>Compares two answers.</summary>
    public static bool operator !=(Outcome left, Outcome right) => !left.Equals(other: right);
}
