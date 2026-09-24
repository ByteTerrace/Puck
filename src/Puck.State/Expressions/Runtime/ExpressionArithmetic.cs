using System.Numerics;
using System.Buffers.Binary;
using Puck.Maths;

namespace Puck.State;

/// <summary>Allocation-free arithmetic refusal for the shared rule, decision, and flock expression evaluator.</summary>
public static partial class ExpressionArithmetic {
    /// <summary>Evaluates one binary operation. Integer division truncates toward zero; Fixed multiplication and
    /// division round once to nearest, ties to even, through Puck.Maths. Overflow is tested after rounding.</summary>
    /// <param name="operation">A binary arithmetic expression operation.</param>
    /// <param name="kind">Int or Fixed (raw Q48.16).</param>
    /// <param name="left">The left raw operand.</param>
    /// <param name="right">The right raw operand.</param>
    /// <param name="value">The raw result, or zero on refusal.</param>
    /// <returns>False for an unsupported operation/kind, zero divisor, invalid bit-operation argument, or unrepresentable result.</returns>
    public static bool TryBinary(ExpressionOp operation, CellKind kind, long left, long right, out long value) {
        value = 0;
        if (kind is not (CellKind.Int or CellKind.Fixed)) { return false; }
        switch (operation) {
            case ExpressionOp.Add:
                return Narrow(
                value: out value,
                wide: (((Int128)left) + right)
            );
            case ExpressionOp.Subtract:
                return Narrow(
                value: out value,
                wide: (((Int128)left) - right)
            );
            case ExpressionOp.Minimum:
                value = Math.Min(
                val1: left,
                val2: right
            ); return true;
            case ExpressionOp.Maximum:
                value = Math.Max(
                val1: left,
                val2: right
            ); return true;
            case ExpressionOp.Equal:
                value = ((left == right)
                ? 1L
                : 0L
            ); return true;
            case ExpressionOp.NotEqual:
                value = ((left != right)
                ? 1L
                : 0L
            ); return true;
            case ExpressionOp.Less:
                value = ((left < right)
                ? 1L
                : 0L
            ); return true;
            case ExpressionOp.LessOrEqual:
                value = ((left <= right)
                ? 1L
                : 0L
            ); return true;
            case ExpressionOp.Greater:
                value = ((left > right)
                ? 1L
                : 0L
            ); return true;
            case ExpressionOp.GreaterOrEqual:
                value = ((left >= right)
                ? 1L
                : 0L
            ); return true;
            case ExpressionOp.Remainder:
                // The raw remainder is the remainder in either kind (Q48.16 bits share one scale); -1 divides
                // everything, so it reads zero rather than faulting on long.MinValue.
                if (right == 0) { return false; }
                value = ((right == -1)
                    ? 0L
                    : (left % right)
                );
                return true;
            case ExpressionOp.BitAnd: if (kind != CellKind.Int) { return false; } value = left & right; return true;
            case ExpressionOp.BitOr: if (kind != CellKind.Int) { return false; } value = left | right; return true;
            case ExpressionOp.BitXor: if (kind != CellKind.Int) { return false; } value = left ^ right; return true;
            case ExpressionOp.ShiftLeft:
                if (
                    (kind != CellKind.Int) ||
                    (((ulong)right) > 63UL)
                ) { return false; }
                value = (left << ((int)right));
                return true;
            case ExpressionOp.ShiftRight:
                if (
                    (kind != CellKind.Int) ||
                    (((ulong)right) > 63UL)
                ) { return false; }
                value = (left >> ((int)right));
                return true;
            case ExpressionOp.ShiftRightLogical:
                if (
                    (kind != CellKind.Int) ||
                    (((ulong)right) > 63UL)
                ) { return false; }
                value = (left >>> ((int)right));
                return true;
            case ExpressionOp.RotateLeft:
                if (
                    (kind != CellKind.Int) ||
                    (((ulong)right) > 63UL)
                ) { return false; }
                value = ((long)BitOperations.RotateLeft(
                    offset: ((int)right),
                    value: ((ulong)left)
                ));
                return true;
            case ExpressionOp.RotateRight:
                if (
                    (kind != CellKind.Int) ||
                    (((ulong)right) > 63UL)
                ) { return false; }
                value = ((long)BitOperations.RotateRight(
                    offset: ((int)right),
                    value: ((ulong)left)
                ));
                return true;
            case ExpressionOp.ParallelBitExtract:
                if (kind != CellKind.Int) { return false; }
                value = ((long)((ulong)left).ParallelBitExtract(mask: ((ulong)right)));
                return true;
            case ExpressionOp.ParallelBitDeposit:
                if (kind != CellKind.Int) { return false; }
                value = ((long)((ulong)left).ParallelBitDeposit(mask: ((ulong)right)));
                return true;
            case ExpressionOp.RepeatBits:
                if (
                    (kind != CellKind.Int) ||
                    !IsReplicationWidth(width: right)
                ) { return false; }
                if (
                    (right != 64) &&
                    ((((ulong)left) >> ((int)right)) != 0UL)
                ) { return false; }
                value = unchecked((long)((ulong)left).RepeatBits(blockWidth: ((int)right)));
                return true;
            case ExpressionOp.Multiply:
                return ((kind == CellKind.Int)
                    ? Narrow(
                        value: out value,
                        wide: (((Int128)left) * right)
                    )
                    : FusedArithmetic.TryMixedScaleProduct(
                        a: left,
                        b: right,
                        fractionBitsA: FixedQ4816.FractionBitCount,
                        fractionBitsB: FixedQ4816.FractionBitCount,
                        fractionBitsOut: FixedQ4816.FractionBitCount,
                        result: out value
                    )
                );
            case ExpressionOp.Divide:
                if (right == 0) { return false; }
                if (kind == CellKind.Int) {
                    return Narrow(
                    value: out value,
                    wide: (((Int128)left) / right)
                );
                }
                var numerator = ((UInt128)((left < 0)
                    ? -((Int128)left)
                    : left));
                var denominator = ((UInt128)((right < 0)
                    ? -((Int128)right)
                    : right));
                if (!FusedArithmetic.TryDivideMagnitudeRounded(
                    denominatorMagnitude: denominator,
                    fractionBitCount: FixedQ4816.FractionBitCount,
                    numeratorMagnitude: numerator,
                    quotient: out var magnitude
                )) { return false; }
                // A 64-bit magnitude with 16 fractional bits is at most 2^79, safely inside Int128.
                return Narrow(
                    value: out value,
                    wide: (((left < 0) != (right < 0))
                    ? -((Int128)magnitude)
                    : (Int128)magnitude)
                );
            default: return false;
        }
    }
    /// <summary>Gets a value indicating whether a core arithmetic operator consumes one stack value (functions have their own dispatcher).</summary>
    /// <param name="operation">An expression operation.</param>
    public static bool IsUnary(ExpressionOp operation) => (ExpressionOperators.Find(operation: operation) is { Function: false, Arity: 1 });
    /// <summary>Evaluates one unary operation. Bit operations read the Int carrier's two's-complement bits; Negate and
    /// Abs keep the operand's kind and refuse the carrier's minimum; Sign yields Int -1, 0, or 1 for either kind.</summary>
    /// <param name="operation">A unary expression operation.</param>
    /// <param name="kind">Int or Fixed (raw Q48.16).</param>
    /// <param name="operand">The raw operand.</param>
    /// <param name="value">The raw result, or zero on refusal.</param>
    /// <returns>False for an unsupported operation/kind, invalid replication width, or unrepresentable result.</returns>
    public static bool TryUnary(ExpressionOp operation, CellKind kind, long operand, out long value) {
        value = 0;
        if (kind is not (CellKind.Int or CellKind.Fixed)) { return false; }
        switch (operation) {
            case ExpressionOp.Negate:
                if (operand == long.MinValue) { return false; }
                value = -operand; return true;
            case ExpressionOp.Absolute:
                if (operand == long.MinValue) { return false; }
                value = Math.Abs(value: operand); return true;
            case ExpressionOp.Sign: value = Math.Sign(value: operand); return true;
        }
        if (kind != CellKind.Int) { return false; }
        var bits = ((ulong)operand);

        switch (operation) {
            case ExpressionOp.BitNot: value = ~operand; return true;
            case ExpressionOp.SetBitCount: value = BitOperations.PopCount(value: bits); return true;
            case ExpressionOp.LeadingZeroCount: value = BitOperations.LeadingZeroCount(value: bits); return true;
            case ExpressionOp.TrailingZeroCount: value = BitOperations.TrailingZeroCount(value: bits); return true;
            case ExpressionOp.LowestSetBit: value = ((long)bits.LowestSetBit()); return true;
            case ExpressionOp.ClearLowestSetBit: value = ((long)bits.ClearLowestSetBit()); return true;
            case ExpressionOp.ByteSwap: value = ((long)BinaryPrimitives.ReverseEndianness(value: bits)); return true;
            case ExpressionOp.ReverseBits: value = ((long)bits.ReverseBits()); return true;
            case ExpressionOp.ReplicationMask:
                if (!IsReplicationWidth(width: operand)) { return false; }
                value = unchecked((long)((int)operand).ReplicationMask<ulong>());
                return true;
            default: return false;
        }
    }
    /// <summary>Extracts the unsigned bit field of <paramref name="width"/> bits at <paramref name="offset"/>.</summary>
    /// <param name="value">The Int carrier.</param>
    /// <param name="offset">The field's lowest bit, 0..63.</param>
    /// <param name="width">The field's width, 1..64, with offset + width at most 64.</param>
    /// <param name="field">The field, or zero on refusal.</param>
    /// <returns>False when the field does not fit the carrier.</returns>
    public static bool TryBitField(long value, long offset, long width, out long field) {
        field = 0;
        if (
            (((ulong)offset) > 63UL) ||
            (width < 1) ||
            (width > 64) ||
            ((offset + width) > 64)
        ) { return false; }
        field = ((long)((((ulong)value) >> ((int)offset)) & ((int)width).LowMask<ulong>()));
        return true;
    }
    /// <summary>Replaces the bit field of <paramref name="width"/> bits at <paramref name="offset"/> with the low
    /// bits of <paramref name="field"/>.</summary>
    /// <param name="value">The Int carrier.</param>
    /// <param name="field">The replacement bits; those above the width are ignored.</param>
    /// <param name="offset">The field's lowest bit, 0..63.</param>
    /// <param name="width">The field's width, 1..64, with offset + width at most 64.</param>
    /// <param name="inserted">The carrier with the field replaced, or zero on refusal.</param>
    /// <returns>False when the field does not fit the carrier.</returns>
    public static bool TryBitInsert(long value, long field, long offset, long width, out long inserted) {
        inserted = 0;
        if (
            (((ulong)offset) > 63UL) ||
            (width < 1) ||
            (width > 64) ||
            ((offset + width) > 64)
        ) { return false; }
        var mask = (((int)width).LowMask<ulong>() << ((int)offset));

        inserted = ((long)((((ulong)value) & ~mask) | ((((ulong)field) << ((int)offset)) & mask)));
        return true;
    }

    // Every width from one bit through the whole word; a width that does not divide 64 truncates its last copy.
    private static bool IsReplicationWidth(long width) => (width is >= 1 and <= 64);
    private static bool Narrow(Int128 wide, out long value) {
        if (
            (wide < long.MinValue) ||
            (wide > long.MaxValue)
        ) { value = 0; return false; }
        value = ((long)wide);
        return true;
    }
}
