namespace Puck.State;

/// <summary>One opcode in a compiled numeric world-rule expression.</summary>
public enum WorldExpressionOp : byte {
    /// <summary>Push a compile-time literal.</summary>
    Constant,
    /// <summary>Push a live state/channel operand.</summary>
    Operand,
    /// <summary>Add.</summary>
    Add,
    /// <summary>Subtract.</summary>
    Subtract,
    /// <summary>Multiply.</summary>
    Multiply,
    /// <summary>Divide.</summary>
    Divide,
    /// <summary>Minimum.</summary>
    Minimum,
    /// <summary>Maximum.</summary>
    Maximum,
    /// <summary>Inclusive clamp.</summary>
    Clamp,
    /// <summary>Remainder, truncating toward zero.</summary>
    Modulo,
    /// <summary>Bitwise AND (Int).</summary>
    BitAnd,
    /// <summary>Bitwise OR (Int).</summary>
    BitOr,
    /// <summary>Bitwise XOR (Int).</summary>
    BitXor,
    /// <summary>Bitwise complement (Int, unary).</summary>
    BitNot,
    /// <summary>Left shift by 0..63 (Int).</summary>
    ShiftLeft,
    /// <summary>Arithmetic right shift by 0..63 (Int).</summary>
    ShiftRight,
    /// <summary>Logical right shift by 0..63 (Int).</summary>
    ShiftRightLogical,
    /// <summary>Equality, pushing Int 1 or 0.</summary>
    Equal,
    /// <summary>Inequality, pushing Int 1 or 0.</summary>
    NotEqual,
    /// <summary>Less-than, pushing Int 1 or 0.</summary>
    Less,
    /// <summary>Less-or-equal, pushing Int 1 or 0.</summary>
    LessOrEqual,
    /// <summary>Greater-than, pushing Int 1 or 0.</summary>
    Greater,
    /// <summary>Greater-or-equal, pushing Int 1 or 0.</summary>
    GreaterOrEqual,
    /// <summary>Conditional choice: condition, whenTrue, whenFalse.</summary>
    Select,
    /// <summary>Set-bit count (Int, unary).</summary>
    PopCount,
    /// <summary>Leading zero count (Int, unary).</summary>
    LeadingZeroCount,
    /// <summary>Trailing zero count (Int, unary).</summary>
    TrailingZeroCount,
    /// <summary>Lowest set bit isolated (Int, unary).</summary>
    LowestSetBit,
    /// <summary>Lowest set bit cleared (Int, unary).</summary>
    ClearLowestSetBit,
    /// <summary>64-bit left rotation by 0..63 (Int).</summary>
    RotateLeft,
    /// <summary>64-bit right rotation by 0..63 (Int).</summary>
    RotateRight,
    /// <summary>Byte order reversed (Int, unary).</summary>
    ByteSwap,
    /// <summary>Bit order reversed (Int, unary).</summary>
    BitReverse,
    /// <summary>Negation in the operand's kind (unary).</summary>
    Negate,
    /// <summary>Magnitude in the operand's kind (unary).</summary>
    Abs,
    /// <summary>Parallel bit extract: the bits of value under the mask, packed low (Int).</summary>
    ParallelBitExtract,
    /// <summary>Parallel bit deposit: the low bits of value scattered to the mask's set positions (Int).</summary>
    ParallelBitDeposit,
    /// <summary>Bit-field extract: value, offset, width (Int).</summary>
    BitField,
    /// <summary>Bit-field insert: value, field, offset, width (Int).</summary>
    BitInsert,
    /// <summary>Topology-aware mask shift: every set bit moves to its neighbour in the compiled direction, and a bit
    /// with no neighbour that way is dropped rather than wrapped (Int, unary).</summary>
    BoardShift,
    /// <summary>A mask carried through one point-group element of the compiled topology (Int, unary).</summary>
    BoardImage,
    /// <summary>Sign as Int -1, 0, 1 (unary, either kind).</summary>
    Sign,
}
