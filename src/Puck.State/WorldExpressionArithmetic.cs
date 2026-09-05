using System.Numerics;
using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;
using Puck.Maths;

namespace Puck.State;

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
            case WorldExpressionOp.Equal: value = (left == right) ? 1L : 0L; return true;
            case WorldExpressionOp.NotEqual: value = (left != right) ? 1L : 0L; return true;
            case WorldExpressionOp.Less: value = (left < right) ? 1L : 0L; return true;
            case WorldExpressionOp.LessOrEqual: value = (left <= right) ? 1L : 0L; return true;
            case WorldExpressionOp.Greater: value = (left > right) ? 1L : 0L; return true;
            case WorldExpressionOp.GreaterOrEqual: value = (left >= right) ? 1L : 0L; return true;
            case WorldExpressionOp.Modulo:
                // The raw remainder is the remainder in either kind (Q48.16 bits share one scale); -1 divides
                // everything, so it reads zero rather than faulting on long.MinValue.
                if (right == 0) { return false; }
                value = (right == -1) ? 0L : (left % right);
                return true;
            case WorldExpressionOp.BitAnd: if (kind != CellKind.Int) { return false; } value = left & right; return true;
            case WorldExpressionOp.BitOr: if (kind != CellKind.Int) { return false; } value = left | right; return true;
            case WorldExpressionOp.BitXor: if (kind != CellKind.Int) { return false; } value = left ^ right; return true;
            case WorldExpressionOp.ShiftLeft:
                if (kind != CellKind.Int || (ulong)right > 63UL) { return false; }
                value = left << (int)right;
                return true;
            case WorldExpressionOp.ShiftRight:
                if (kind != CellKind.Int || (ulong)right > 63UL) { return false; }
                value = left >> (int)right;
                return true;
            case WorldExpressionOp.ShiftRightLogical:
                if (kind != CellKind.Int || (ulong)right > 63UL) { return false; }
                value = left >>> (int)right;
                return true;
            case WorldExpressionOp.RotateLeft:
                if (kind != CellKind.Int || (ulong)right > 63UL) { return false; }
                value = (long)BitOperations.RotateLeft((ulong)left, (int)right);
                return true;
            case WorldExpressionOp.RotateRight:
                if (kind != CellKind.Int || (ulong)right > 63UL) { return false; }
                value = (long)BitOperations.RotateRight((ulong)left, (int)right);
                return true;
            case WorldExpressionOp.ParallelBitExtract:
                if (kind != CellKind.Int) { return false; }
                value = (long)ParallelExtract((ulong)left, (ulong)right);
                return true;
            case WorldExpressionOp.ParallelBitDeposit:
                if (kind != CellKind.Int) { return false; }
                value = (long)ParallelDeposit((ulong)left, (ulong)right);
                return true;
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

    /// <summary>Gets a value indicating whether an operation consumes one stack value.</summary>
    /// <param name="operation">An expression operation.</param>
    public static bool IsUnary(WorldExpressionOp operation) => operation is WorldExpressionOp.BitNot or WorldExpressionOp.PopCount
        or WorldExpressionOp.LeadingZeroCount or WorldExpressionOp.TrailingZeroCount or WorldExpressionOp.LowestSetBit
        or WorldExpressionOp.ClearLowestSetBit or WorldExpressionOp.ByteSwap or WorldExpressionOp.BitReverse
        or WorldExpressionOp.Negate or WorldExpressionOp.Abs or WorldExpressionOp.Sign;

    /// <summary>Evaluates one unary operation. Bit operations read the Int carrier's two's-complement bits; Negate and
    /// Abs keep the operand's kind and refuse the carrier's minimum; Sign yields Int -1, 0, or 1 for either kind.</summary>
    /// <param name="operation">A unary expression operation.</param>
    /// <param name="kind">Int or Fixed (raw Q48.16).</param>
    /// <param name="operand">The raw operand.</param>
    /// <param name="value">The raw result, or zero on refusal.</param>
    /// <returns>False for an unsupported operation/kind or an unrepresentable result.</returns>
    public static bool TryUnary(WorldExpressionOp operation, CellKind kind, long operand, out long value) {
        value = 0;
        if (kind is not (CellKind.Int or CellKind.Fixed)) { return false; }
        switch (operation) {
            case WorldExpressionOp.Negate:
                if (operand == long.MinValue) { return false; }
                value = -operand; return true;
            case WorldExpressionOp.Abs:
                if (operand == long.MinValue) { return false; }
                value = Math.Abs(operand); return true;
            case WorldExpressionOp.Sign: value = Math.Sign(operand); return true;
        }
        if (kind != CellKind.Int) { return false; }
        var bits = (ulong)operand;
        switch (operation) {
            case WorldExpressionOp.BitNot: value = ~operand; return true;
            case WorldExpressionOp.PopCount: value = BitOperations.PopCount(bits); return true;
            case WorldExpressionOp.LeadingZeroCount: value = BitOperations.LeadingZeroCount(bits); return true;
            case WorldExpressionOp.TrailingZeroCount: value = BitOperations.TrailingZeroCount(bits); return true;
            case WorldExpressionOp.LowestSetBit: value = (long)(bits & (~bits + 1UL)); return true;
            case WorldExpressionOp.ClearLowestSetBit: value = (long)(bits & (bits - 1UL)); return true;
            case WorldExpressionOp.ByteSwap: value = (long)BinaryPrimitives.ReverseEndianness(bits); return true;
            case WorldExpressionOp.BitReverse: value = (long)ReverseBits(bits); return true;
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
        if ((ulong)offset > 63UL || width < 1 || width > 64 || offset + width > 64) { return false; }
        field = (long)(((ulong)value >> (int)offset) & FieldMask((int)width));
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
        if ((ulong)offset > 63UL || width < 1 || width > 64 || offset + width > 64) { return false; }
        var mask = FieldMask((int)width) << (int)offset;
        inserted = (long)(((ulong)value & ~mask) | (((ulong)field << (int)offset) & mask));
        return true;
    }

    private static ulong FieldMask(int width) => (width == 64) ? ulong.MaxValue : ((1UL << width) - 1UL);

    // BMI2 when the machine has it; the software forms walk the mask's set bits from the bottom and are bit-exact
    // with the instructions, so a replay agrees across machines either way.
    private static ulong ParallelExtract(ulong value, ulong mask) {
        if (Bmi2.X64.IsSupported) {
            return Bmi2.X64.ParallelBitExtract(value, mask);
        }
        var result = 0UL;
        var bit = 0;
        while (mask != 0UL) {
            var lowest = mask & (~mask + 1UL);
            if ((value & lowest) != 0UL) { result |= 1UL << bit; }
            bit++;
            mask &= mask - 1UL;
        }
        return result;
    }
    private static ulong ParallelDeposit(ulong value, ulong mask) {
        if (Bmi2.X64.IsSupported) {
            return Bmi2.X64.ParallelBitDeposit(value, mask);
        }
        var result = 0UL;
        var bit = 0;
        while (mask != 0UL) {
            var lowest = mask & (~mask + 1UL);
            if (((value >> bit) & 1UL) != 0UL) { result |= lowest; }
            bit++;
            mask &= mask - 1UL;
        }
        return result;
    }

    // Bit reversal by successive swaps of halves, pairs, nibbles, then the byte order.
    private static ulong ReverseBits(ulong bits) {
        bits = ((bits >> 1) & 0x5555555555555555UL) | ((bits & 0x5555555555555555UL) << 1);
        bits = ((bits >> 2) & 0x3333333333333333UL) | ((bits & 0x3333333333333333UL) << 2);
        bits = ((bits >> 4) & 0x0F0F0F0F0F0F0F0FUL) | ((bits & 0x0F0F0F0F0F0F0F0FUL) << 4);
        return BinaryPrimitives.ReverseEndianness(bits);
    }

    private static bool Narrow(Int128 wide, out long value) {
        if (wide < long.MinValue || wide > long.MaxValue) { value = 0; return false; }
        value = (long)wide;
        return true;
    }
}
