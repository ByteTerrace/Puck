namespace Puck.State;

/// <summary>One opcode in a compiled numeric world-rule expression.</summary>
public enum ExpressionOp : byte {
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
    /// <summary>Szudzik's pairing of two non-negative integers into one; <c>pairX</c>/<c>pairY</c> invert it.</summary>
    Pair,
    /// <summary>The first component of a paired value.</summary>
    PairX,
    /// <summary>The second component of a paired value.</summary>
    PairY,
    /// <summary>The pair with its components exchanged, computed without unpairing.</summary>
    PairSwap,
    /// <summary>The larger component of a paired value.</summary>
    PairMax,
    /// <summary>The smaller component of a paired value.</summary>
    PairMin,
    /// <summary>The sum of a paired value's components.</summary>
    PairSum,
    /// <summary>The absolute difference of a paired value's components.</summary>
    PairDifference,
    /// <summary>The pair with both components moved by the second argument.</summary>
    PairTranslate,
    /// <summary>The pair with both components multiplied by the second argument.</summary>
    PairScale,
    /// <summary>Bit-interleaves two non-negative integers below 2^31 (x on the even bits); <c>mortonX</c>/<c>mortonY</c> invert it.</summary>
    Morton,
    /// <summary>The even-bit component of a Morton code.</summary>
    MortonX,
    /// <summary>The odd-bit component of a Morton code.</summary>
    MortonY,
    /// <summary>The distance along the Hilbert curve of the given order (1..31) to (x, y); <c>hilbertX</c>/<c>hilbertY</c> invert it.</summary>
    Hilbert,
    /// <summary>The x of the point at a Hilbert distance for the given order.</summary>
    HilbertX,
    /// <summary>The y of the point at a Hilbert distance for the given order.</summary>
    HilbertY,
    /// <summary>The ring-ordered index of the hex cell at Eisenstein coordinates (q, r); <c>hexQ</c>/<c>hexR</c> invert it.</summary>
    HexIndex,
    /// <summary>The q coordinate of a hex index.</summary>
    HexQ,
    /// <summary>The r coordinate of a hex index.</summary>
    HexR,
    /// <summary>The ring a hex index lies on.</summary>
    HexRadius,
    /// <summary>The Eisenstein norm q² − q·r + r² of a hex index.</summary>
    HexNorm,
    /// <summary>The step distance between two hex indices.</summary>
    HexDistance,
    /// <summary>The hex index one step away in direction 0..5 (counterclockwise from +q; any integer, taken modulo 6).</summary>
    HexNeighbor,
    /// <summary>The hex index rotated about the origin by sixth turns (any integer, taken modulo 6).</summary>
    HexRotate,
    /// <summary>The hex index reflected across the q axis.</summary>
    HexConjugate,
    /// <summary>The hex index with q and r exchanged.</summary>
    HexSwap,
    /// <summary>The hex index of the coordinate sum.</summary>
    HexAdd,
    /// <summary>The hex index of the coordinate difference.</summary>
    HexSubtract,
    /// <summary>The hex index of the Eisenstein product.</summary>
    HexMultiply,
    /// <summary>The hex index scaled by an integer.</summary>
    HexScale,
    /// <summary>The hex index moved by (q, r).</summary>
    HexTranslate,
    /// <summary>The layer holding an index in a layer sequence (index, start, step, seed): a core of seed indices wrapped by layers of start, start+step, … indices.</summary>
    Layer,
    /// <summary>The position of an index within its layer, for the same (index, start, step, seed) sequence.</summary>
    LayerOffset,
    /// <summary>The first index of a layer, for a (layer, start, step, seed) sequence.</summary>
    LayerStart,
    /// <summary>The index count of a layer, for a (layer, start, step, seed) sequence.</summary>
    LayerSize,
    /// <summary>The square root: the floor root of a non-negative int, or the fixed-point root of a non-negative fixed value.</summary>
    SquareRoot,
    /// <summary>The sine of a fixed-point angle in radians; fixed expressions only.</summary>
    Sine,
    /// <summary>The cosine of a fixed-point angle in radians; fixed expressions only.</summary>
    Cosine,
}
