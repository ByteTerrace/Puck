using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>One opcode in a compiled numeric world-rule expression, and the one vocabulary any document that spells
/// an arithmetic operation reads from — a cartridge's <c>set</c> step names its combining operation here rather than
/// restating the list, so the two can never grow apart.</summary>
[JsonConverter(typeof(StrictEnumConverter<ExpressionOp>))]
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
    Remainder,
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
    SetBitCount,
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
    ReverseBits,
    /// <summary>Negation in the operand's kind (unary).</summary>
    Negate,
    /// <summary>Magnitude in the operand's kind (unary).</summary>
    Absolute,
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
    /// <summary>Topology-aware mask ray: the union of a mask and every repeated shift of it in the compiled direction
    /// until the edge — a file, a rank, or a diagonal from one seed bit, with no hand-written constant (Int, unary).</summary>
    BoardRay,
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
    PairMaximum,
    /// <summary>The smaller component of a paired value.</summary>
    PairMinimum,
    /// <summary>The sum of a paired value's components.</summary>
    PairSum,
    /// <summary>The absolute difference of a paired value's components.</summary>
    PairDifference,
    /// <summary>The pair with both components moved by the second argument.</summary>
    PairTranslate,
    /// <summary>The pair with both components multiplied by the second argument.</summary>
    PairScale,
    /// <summary>Bit-interleaves two non-negative integers below 2^31 (x on the even bits); <c>mortonX</c>/<c>mortonY</c> invert it.</summary>
    MortonIndex,
    /// <summary>The even-bit component of a Morton code.</summary>
    MortonX,
    /// <summary>The odd-bit component of a Morton code.</summary>
    MortonY,
    /// <summary>The distance along the Hilbert curve of the given order (1..31) to (x, y); <c>hilbertX</c>/<c>hilbertY</c> invert it.</summary>
    HilbertIndex,
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
    /// <summary>The Eisenstein squared straight-line distance from the origin q² − q·r + r² of a hex index.</summary>
    HexEuclideanSquared,
    /// <summary>The step distance between two hex indices.</summary>
    HexDistance,
    /// <summary>The hex index one step away in direction 0..5 (counterclockwise from +q; any integer, taken modulo 6).</summary>
    HexNeighbor,
    /// <summary>The hex index rotated about the origin by sixth turns (any integer, taken modulo 6).</summary>
    HexRotate,
    /// <summary>The hex index reflected across the q axis.</summary>
    HexMirror,
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
    /// <summary>The shell-ordered index of the square cell at Gaussian coordinates (x, y); <c>squareX</c>/<c>squareY</c> invert it.</summary>
    SquareIndex,
    /// <summary>The x coordinate of a square index.</summary>
    SquareX,
    /// <summary>The y coordinate of a square index.</summary>
    SquareY,
    /// <summary>The shell a square index lies on: its Chebyshev distance from the origin.</summary>
    SquareRadius,
    /// <summary>The Manhattan distance of a square index from the origin.</summary>
    SquareLength,
    /// <summary>The Gaussian squared straight-line distance from the origin x² + y² of a square index.</summary>
    SquareEuclideanSquared,
    /// <summary>The Manhattan (rook-step) distance between two square indices.</summary>
    SquareDistance,
    /// <summary>The Chebyshev (king-step) distance between two square indices.</summary>
    SquareChebyshev,
    /// <summary>The square index one step away in direction 0..3 (east, north, west, south; any integer, taken modulo 4).</summary>
    SquareNeighbor,
    /// <summary>The square index rotated about the origin by quarter turns (any integer, taken modulo 4).</summary>
    SquareRotate,
    /// <summary>The square index reflected across the x axis.</summary>
    SquareMirror,
    /// <summary>The square index with x and y exchanged.</summary>
    SquareSwap,
    /// <summary>The square index of the coordinate sum.</summary>
    SquareAdd,
    /// <summary>The square index of the coordinate difference.</summary>
    SquareSubtract,
    /// <summary>The square index of the Gaussian product.</summary>
    SquareMultiply,
    /// <summary>The square index scaled by an integer.</summary>
    SquareScale,
    /// <summary>The square index moved by (x, y).</summary>
    SquareTranslate,
    /// <summary>The greatest common divisor of two integers' magnitudes; gcd(dx, dy) == 1 is a lattice line with no interior point.</summary>
    GreatestCommonDivisor,
    /// <summary>The least common multiple of two integers' magnitudes.</summary>
    LeastCommonMultiple,
    /// <summary>The floored remainder of a by m: for a positive m the value in [0, m) congruent to a, whatever a's sign.</summary>
    FloorModulo,
    /// <summary>The forward (clockwise) distance from a to b around an m-cycle: mod(b − a, m).</summary>
    CycleForward,
    /// <summary>The shortest distance between a and b around an m-cycle, in either direction.</summary>
    CycleDistance,
    /// <summary>The smallest non-negative integer missing from a 64-bit set — the lowest clear bit (the Sprague–Grundy value of a position whose options' values are the set).</summary>
    SmallestMissing,
    /// <summary>Whether a non-negative integer is prime, decided exactly over the whole 64-bit range in bounded work.</summary>
    IsPrime,
    /// <summary>The i-th prime for i in 0..255 (2, 3, 5, … 1619): the bounded table a Gödel multiset or a coprime stride reaches for.</summary>
    PrimeAt,
    /// <summary>n choose k, exact; zero when k exceeds n.</summary>
    BinomialCoefficient,
    /// <summary>n! for n in 0..20.</summary>
    Factorial,
    /// <summary>The colexicographic rank of a k-subset of 0..n−1 given as a bitmask (bit e set = element e chosen), in [0, choose(n, k)); n at most 64.</summary>
    SubsetRank,
    /// <summary>The k-subset of 0..n−1 at a colexicographic rank, as a bitmask; the inverse of subsetRank.</summary>
    SubsetAt,
    /// <summary>The i-th smallest element (i from 0) of the k-subset of 0..n−1 at a colexicographic rank, without building the subset.</summary>
    SubsetMember,
    /// <summary>The lexicographic (Lehmer) rank of a permutation of 0..n−1 packed as nibbles — position i in bits 4i..4i+3 — in [0, n!); n at most 16.</summary>
    ArrangementRank,
    /// <summary>The permutation of 0..n−1 at a lexicographic rank, packed as nibbles; the inverse of arrangementRank.</summary>
    ArrangementAt,
    /// <summary>The element at position i of the permutation of 0..n−1 at a lexicographic rank.</summary>
    ArrangementMember,
    /// <summary>The largest whole number no greater than the value; the identity on an Int operand (unary).</summary>
    Floor,
    /// <summary>The smallest whole number no less than the value; the identity on an Int operand (unary).</summary>
    Ceiling,
    /// <summary>The nearest whole number, halves to even; the identity on an Int operand (unary).</summary>
    Round,
    /// <summary>A 64-bit mask with one bit at the bottom of each block; the width must divide 64.</summary>
    ReplicationMask,
    /// <summary>A pattern repeated across 64 bits; the width must divide 64 and the pattern must fit its block.</summary>
    RepeatBits,
    /// <summary>The dot product of two vectors of the same space: an integer sum of component products.</summary>
    Dot,
    /// <summary>The cosine similarity of two vectors of the same space, in Q48.16 (Fixed); fixed expressions only.</summary>
    Similarity,
    /// <summary>Whether two vectors of the same space are identical component-by-component; 1 when equal, 0 otherwise.</summary>
    Identical,
    /// <summary>Whether the operand beneath it named no cell: Int 1 for an absent fact, 0 for any value (unary).
    /// The one operation whose operand may be absent without the program refusing.</summary>
    IsAbsent,
    /// <summary>The left operand when it names a cell, else the right: the authored answer to what an absent
    /// endpoint means. Both arms share a kind; the result takes it.</summary>
    Coalesce,
    /// <summary>Folds a family, pushing Int 1 when the subprogram is nonzero for every member, else 0.</summary>
    All,
    /// <summary>Folds a family, pushing Int 1 when the subprogram is nonzero for some member, else 0.</summary>
    Any,
    /// <summary>Folds a family, pushing the Int count of members whose subprogram is nonzero.</summary>
    Count,
    /// <summary>Folds a family, pushing the sum of the subprogram over every member.</summary>
    Sum,
    /// <summary>Calls a subprogram of the same program, consuming its arity from the stack and pushing its one
    /// result. The call graph is a DAG, so the runtime never recurses.</summary>
    Call,
    /// <summary>Pushes one of the arguments the call in flight consumed, inside a subprogram.</summary>
    Argument,
    /// <summary>Pushes the value of the family member the fold in flight is visiting, inside a fold subprogram.</summary>
    Member,
}
