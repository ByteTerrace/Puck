using Puck.Maths;

namespace Puck.World.Server;

/// <summary>Allocation-free arithmetic refusal for the shared rule, decision, and flock expression evaluator.</summary>
public static class WorldExpressionArithmetic {
    /// <summary>Evaluates one binary operation. Integer division truncates toward zero; Fixed multiplication and
    /// division round once to nearest, ties to even, through Puck.Maths. Overflow is tested after rounding.</summary>
    /// <param name="operation">A binary arithmetic expression operation.</param>
    /// <param name="kind">Int or Fixed (raw Q48.16).</param>
    /// <param name="left">The left raw operand.</param>
    /// <param name="right">The right raw operand.</param>
    /// <param name="value">The raw result, or zero on refusal.</param>
    /// <returns>False for an unsupported operation/kind, zero divisor, or unrepresentable result.</returns>
    public static bool TryBinary(WorldExpressionOp operation, CellKind kind, long left, long right, out long value) {
        value = 0;
        if (kind is not (CellKind.Int or CellKind.Fixed)) { return false; }
        switch (operation) {
            case WorldExpressionOp.Add: return Narrow((Int128)left + right, out value);
            case WorldExpressionOp.Subtract: return Narrow((Int128)left - right, out value);
            case WorldExpressionOp.Minimum: value = Math.Min(left, right); return true;
            case WorldExpressionOp.Maximum: value = Math.Max(left, right); return true;
            case WorldExpressionOp.Multiply:
                return kind == CellKind.Int ? Narrow((Int128)left * right, out value) :
                    FusedArithmetic.TryMixedScaleProduct(left, FixedQ4816.FractionBitCount, right,
                        FixedQ4816.FractionBitCount, FixedQ4816.FractionBitCount, out value);
            case WorldExpressionOp.Divide:
                if (right == 0) { return false; }
                if (kind == CellKind.Int) { return Narrow((Int128)left / right, out value); }
                var numerator = (UInt128)(left < 0 ? -(Int128)left : left);
                var denominator = (UInt128)(right < 0 ? -(Int128)right : right);
                if (!FusedArithmetic.TryDivideMagnitudeRounded(numerator, denominator, FixedQ4816.FractionBitCount, out var magnitude)) { return false; }
                // A 64-bit magnitude with 16 fractional bits is at most 2^79, safely inside Int128.
                return Narrow((left < 0) != (right < 0) ? -(Int128)magnitude : (Int128)magnitude, out value);
            default: return false;
        }
    }

    private static bool Narrow(Int128 wide, out long value) {
        if (wide < long.MinValue || wide > long.MaxValue) { value = 0; return false; }
        value = (long)wide;
        return true;
    }
}
