namespace Puck.State;

/// <summary>Why a compiled expression did not evaluate.</summary>
/// <remarks><see cref="Forever"/> and <see cref="Domain"/> are faults, the case an <see cref="Outcome"/> that faulted
/// carries. <see cref="Absent"/> is a value, not a fault: each consumer answers it where it sits, a gate as false and an
/// effect as a refusal naming <c>isAbsent</c> and <c>??</c>.</remarks>
public enum ExpressionFault : byte {
    /// <summary>The expression evaluated.</summary>
    None,
    /// <summary>An operand read a fact with no number.</summary>
    Forever,
    /// <summary>An operand's dynamic key named no cell (an empty zone's endpoint).</summary>
    Absent,
    /// <summary>Overflow, a divide by zero, a function argument outside its domain, or an invalid stack result.</summary>
    Domain,
}
