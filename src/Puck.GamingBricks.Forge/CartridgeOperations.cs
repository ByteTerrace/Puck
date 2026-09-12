using Puck.State;

namespace Puck.GamingBricks.Forge;

/// <summary>Which arms of the engine's shared vocabularies a cartridge admits. The spellings are
/// <see cref="Puck.State"/>'s — <see cref="ActionStateComparison"/> for a rule's question, <see cref="ExpressionOp"/>
/// for a <c>set</c> step's combining operation; this names the emittable subset.
/// KEEP IN SYNC with both backends' operation switches.</summary>
public static class CartridgeOperations {
    /// <summary>Gets the operations a <c>set</c> step may combine with. Absence is plain assignment; no opcode
    /// spells it.</summary>
    public static IReadOnlySet<ExpressionOp> Combines { get; } = new HashSet<ExpressionOp> {
        ExpressionOp.Add,
        ExpressionOp.Subtract,
        ExpressionOp.Multiply,
        ExpressionOp.Divide,
        ExpressionOp.Modulo,
        ExpressionOp.BitAnd,
        ExpressionOp.BitOr,
        ExpressionOp.BitXor,
        ExpressionOp.ShiftLeft,
        ExpressionOp.ShiftRight,
    };

    /// <summary>Gets the admitted operations, comma separated, for a refusal message.</summary>
    public static string CombineNames { get; } = string.Join(
        separator: ", ",
        values: Combines.Select(selector: op => op.ToString()).Order(comparer: StringComparer.Ordinal)
    );

    /// <summary>Returns whether a <c>set</c> step's operation is one this machine can emit.</summary>
    /// <param name="operation">The authored operation, or <see langword="null"/> for plain assignment.</param>
    /// <returns><see langword="true"/> when the step is emittable.</returns>
    public static bool AdmitsCombine(ExpressionOp? operation) => ((operation is not { } op) || Combines.Contains(item: op));

    /// <summary>Returns whether an operation shifts. A literal count of eight or more is refused at validation; a
    /// runtime one yields zero.</summary>
    /// <param name="operation">The authored operation.</param>
    /// <returns><see langword="true"/> for the two shift opcodes.</returns>
    public static bool Shifts(ExpressionOp? operation) => (operation is (ExpressionOp.ShiftLeft or ExpressionOp.ShiftRight));
}
