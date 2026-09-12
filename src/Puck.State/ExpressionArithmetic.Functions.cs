using Puck.Maths;

namespace Puck.State;

public static partial class ExpressionArithmetic {
    // Szudzik's pairing z·(z+1)+… with z = max(x, y) stays inside a signed 64-bit cell while z ≤ this bound.
    private const long MaxPairComponent = 3_037_000_498L;
    private const long MaxMortonComponent = ((1L << 31) - 1L);
    private const int MaxHilbertOrder = 31;
    private const int HexDirections = HexagonalCoordinate.NeighborCount;
    // The first 256 primes, derived once from the same enumeration that decides them — never a literal table.
    private static readonly long[] s_primes = BuildPrimes(count: 256);

    private static long[] BuildPrimes(int count) {
        var primes = new long[count];
        for (var index = 0; index < count; index++) {
            primes[index] = ((uint)index).NthPrime();
        }
        return primes;
    }

    /// <summary>Returns how many values a function token consumes, or 0 for an operator token the arithmetic core
    /// evaluates itself.</summary>
    /// <param name="operation">The operation.</param>
    public static int FunctionArity(ExpressionOp operation) => ExpressionOperators.Find(operation) is { Function: true } descriptor ? descriptor.Arity : 0;

    /// <summary>Gets a value indicating whether a function token is admitted in expressions of <paramref name="kind"/>:
    /// <c>sqrt</c> in both kinds, <c>sin</c>/<c>cos</c> in fixed only, every other function in int only.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="kind">The expression kind.</param>
    public static bool FunctionAdmits(ExpressionOp operation, CellKind kind) => ExpressionOperators.Find(operation) is { Function: true } descriptor && descriptor.Admits(kind);

    /// <summary>Evaluates a function token over its arguments. A domain fault (a negative index, a component past the
    /// cell's capacity, an order outside 1..31, a layer sequence constant outside its range) fails the expression
    /// the way an overflow does, never wraps.</summary>
    /// <param name="operation">The function.</param>
    /// <param name="kind">The expression kind.</param>
    /// <param name="arguments">The arguments, in authored order.</param>
    /// <param name="value">The result.</param>
    public static bool TryFunction(ExpressionOp operation, CellKind kind, ReadOnlySpan<long> arguments, out long value) {
        value = 0L;
        if (!FunctionAdmits(operation: operation, kind: kind) || (arguments.Length != FunctionArity(operation: operation))) {
            return false;
        }

        return TryValidatedFunction(operation, kind, arguments, out value);
    }

    // The compiler proved kind and arity; dynamic arithmetic domains are still checked below.
    internal static bool TryValidatedFunction(ExpressionOp operation, CellKind kind, ReadOnlySpan<long> arguments, out long value) {
        value = 0L;
        try {
            switch (operation) {
                case ExpressionOp.SquareRoot:
                    if (arguments[0] < 0L) { return false; }
                    value = ((kind == CellKind.Fixed)
                        ? FixedQ4816.Sqrt(value: FixedQ4816.FromRawBits(value: arguments[0])).Value
                        : ((long)((ulong)arguments[0]).SquareRoot()));
                    return true;
                case ExpressionOp.Floor:
                case ExpressionOp.Ceiling:
                case ExpressionOp.Round:
                    // An int expression is already whole, so all three are the identity there; only a fixed
                    // expression has a fraction to resolve. Ties go to even, the same rule the document language's
                    // own `round` uses, so one spelling never means two roundings.
                    if (kind != CellKind.Fixed) { value = arguments[0]; return true; }
                    value = (operation switch {
                        ExpressionOp.Floor => FixedQ4816.Floor(value: FixedQ4816.FromRawBits(value: arguments[0])),
                        ExpressionOp.Ceiling => FixedQ4816.Ceiling(value: FixedQ4816.FromRawBits(value: arguments[0])),
                        _ => FixedQ4816.Round(value: FixedQ4816.FromRawBits(value: arguments[0])),
                    }).Value;
                    return true;
                case ExpressionOp.Sine:
                    value = FixedQ4816.Sin(angle: FixedQ4816.FromRawBits(value: arguments[0])).Value;
                    return true;
                case ExpressionOp.Cosine:
                    value = FixedQ4816.Cos(angle: FixedQ4816.FromRawBits(value: arguments[0])).Value;
                    return true;
                case ExpressionOp.Pair: {
                    var x = arguments[0];
                    var y = arguments[1];
                    if ((x < 0L) || (y < 0L) || (Math.Max(val1: x, val2: y) > MaxPairComponent)) { return false; }
                    value = ((long)((ulong)x).ElegantPair<ulong, ulong>(other: ((ulong)y)));
                    return true;
                }
                case ExpressionOp.PairX:
                case ExpressionOp.PairY: {
                    if (arguments[0] < 0L) { return false; }
                    var (x, y) = ((ulong)arguments[0]).ElegantUnpair<ulong, ulong>();
                    value = ((long)((operation == ExpressionOp.PairX) ? x : y));
                    return true;
                }
                case ExpressionOp.PairSwap:
                    if (arguments[0] < 0L) { return false; }
                    value = ((long)((ulong)arguments[0]).ElegantSwap());
                    return true;
                case ExpressionOp.PairMax:
                    if (arguments[0] < 0L) { return false; }
                    value = ((long)((ulong)arguments[0]).ElegantMaximum());
                    return true;
                case ExpressionOp.PairMin:
                    if (arguments[0] < 0L) { return false; }
                    value = ((long)((ulong)arguments[0]).ElegantMinimum());
                    return true;
                case ExpressionOp.PairSum:
                    if (arguments[0] < 0L) { return false; }
                    value = ((long)((ulong)arguments[0]).ElegantSum());
                    return true;
                case ExpressionOp.PairDifference:
                    if (arguments[0] < 0L) { return false; }
                    value = ((long)((ulong)arguments[0]).ElegantDifference());
                    return true;
                case ExpressionOp.PairTranslate: {
                    var pair = arguments[0];
                    var amount = arguments[1];
                    if ((pair < 0L) || (amount < 0L)) { return false; }
                    var maximum = ((long)((ulong)pair).ElegantMaximum());
                    if (amount > (MaxPairComponent - maximum)) { return false; }
                    value = ((long)((ulong)pair).ElegantTranslate(amount: ((ulong)amount)));
                    return true;
                }
                case ExpressionOp.PairScale: {
                    var pair = arguments[0];
                    var factor = arguments[1];
                    if ((pair < 0L) || (factor < 0L)) { return false; }
                    var maximum = ((long)((ulong)pair).ElegantMaximum());
                    if ((maximum != 0L) && (factor > (MaxPairComponent / maximum))) { return false; }
                    value = ((long)((ulong)pair).ElegantScale(factor: ((ulong)factor)));
                    return true;
                }
                case ExpressionOp.Morton: {
                    var x = arguments[0];
                    var y = arguments[1];
                    if ((x < 0L) || (y < 0L) || (x > MaxMortonComponent) || (y > MaxMortonComponent)) { return false; }
                    value = ((long)((uint)x).BitwisePair<uint, ulong>(other: ((uint)y)));
                    return true;
                }
                case ExpressionOp.MortonX:
                case ExpressionOp.MortonY: {
                    if (arguments[0] < 0L) { return false; }
                    var (x, y) = ((ulong)arguments[0]).BitwiseUnpair<ulong, uint>();
                    value = ((operation == ExpressionOp.MortonX) ? x : y);
                    return true;
                }
                case ExpressionOp.Hilbert: {
                    var order = arguments[0];
                    var x = arguments[1];
                    var y = arguments[2];
                    if ((order < 1L) || (order > MaxHilbertOrder) || (x < 0L) || (y < 0L) || (x >= (1L << (int)order)) || (y >= (1L << (int)order))) { return false; }
                    value = ((long)HilbertCurve.Encode(order: ((int)order), x: ((uint)x), y: ((uint)y)));
                    return true;
                }
                case ExpressionOp.HilbertX:
                case ExpressionOp.HilbertY: {
                    var order = arguments[0];
                    var distance = arguments[1];
                    if ((order < 1L) || (order > MaxHilbertOrder) || (distance < 0L) || (distance >= (1L << (int)(order << 1)))) { return false; }
                    var (x, y) = HilbertCurve.Decode(order: ((int)order), distance: ((ulong)distance));
                    value = ((operation == ExpressionOp.HilbertX) ? x : y);
                    return true;
                }
                case ExpressionOp.HexIndex:
                    if (!IsInt(arguments[0]) || !IsInt(arguments[1])) { return false; }
                    value = HexagonalIndex.FromCoordinate(coordinate: new HexagonalCoordinate(Q: ((int)arguments[0]), R: ((int)arguments[1]))).Value;
                    return true;
                case ExpressionOp.HexQ:
                case ExpressionOp.HexR: {
                    if (!TryHex(arguments[0], out var cell)) { return false; }
                    var coordinate = cell.ToCoordinate();
                    value = ((operation == ExpressionOp.HexQ) ? coordinate.Q : coordinate.R);
                    return true;
                }
                case ExpressionOp.HexRadius: {
                    if (!TryHex(arguments[0], out var cell)) { return false; }
                    value = cell.Radius;
                    return true;
                }
                case ExpressionOp.HexEuclideanSquared: {
                    if (!TryHex(arguments[0], out var cell)) { return false; }
                    value = cell.Norm;
                    return true;
                }
                case ExpressionOp.HexMirror: {
                    if (!TryHex(arguments[0], out var cell)) { return false; }
                    value = cell.Conjugate().Value;
                    return true;
                }
                case ExpressionOp.HexSwap: {
                    if (!TryHex(arguments[0], out var cell)) { return false; }
                    value = cell.Swap().Value;
                    return true;
                }
                case ExpressionOp.HexDistance: {
                    if (!TryHex(arguments[0], out var left) || !TryHex(arguments[1], out var right)) { return false; }
                    value = HexagonalIndex.Distance(left: left, right: right);
                    return true;
                }
                case ExpressionOp.HexNeighbor: {
                    if (!TryHex(arguments[0], out var cell)) { return false; }
                    value = cell.Neighbor(direction: ((int)arguments[1].FloorModulo(modulus: HexDirections))).Value;
                    return true;
                }
                case ExpressionOp.HexRotate: {
                    if (!TryHex(arguments[0], out var cell)) { return false; }
                    value = cell.Rotate(turns: ((int)arguments[1].FloorModulo(modulus: HexDirections))).Value;
                    return true;
                }
                case ExpressionOp.HexAdd: {
                    if (!TryHex(arguments[0], out var left) || !TryHex(arguments[1], out var right)) { return false; }
                    value = (left + right).Value;
                    return true;
                }
                case ExpressionOp.HexSubtract: {
                    if (!TryHex(arguments[0], out var left) || !TryHex(arguments[1], out var right)) { return false; }
                    value = (left - right).Value;
                    return true;
                }
                case ExpressionOp.HexMultiply: {
                    if (!TryHex(arguments[0], out var left) || !TryHex(arguments[1], out var right)) { return false; }
                    value = (left * right).Value;
                    return true;
                }
                case ExpressionOp.HexScale: {
                    if (!TryHex(arguments[0], out var cell) || !IsInt(arguments[1])) { return false; }
                    value = cell.Scale(factor: ((int)arguments[1])).Value;
                    return true;
                }
                case ExpressionOp.HexTranslate: {
                    if (!TryHex(arguments[0], out var cell) || !IsInt(arguments[1]) || !IsInt(arguments[2])) { return false; }
                    value = cell.Translate(displacement: new HexagonalCoordinate(Q: ((int)arguments[1]), R: ((int)arguments[2]))).Value;
                    return true;
                }
                case ExpressionOp.Layer:
                case ExpressionOp.LayerOffset:
                case ExpressionOp.LayerStart:
                case ExpressionOp.LayerSize: {
                    var subject = arguments[0];
                    if (subject < 0L) { return false; }
                    var sequence = LayerSequence.Create(start: arguments[1], step: arguments[2], seed: arguments[3]);
                    value = operation switch {
                        ExpressionOp.Layer => sequence.LayerOf(index: subject),
                        ExpressionOp.LayerOffset => sequence.Locate(index: subject).Offset,
                        ExpressionOp.LayerStart => ((subject == 0L) ? 0L : sequence.Count(layerCount: (subject - 1L))),
                        _ => sequence.LayerSize(layer: subject),
                    };
                    return true;
                }
                case ExpressionOp.SquareIndex:
                    if (!IsInt(arguments[0]) || !IsInt(arguments[1])) { return false; }
                    value = Puck.Maths.SquareIndex.FromCoordinate(coordinate: new SquareCoordinate(X: ((int)arguments[0]), Y: ((int)arguments[1]))).Value;
                    return true;
                case ExpressionOp.SquareX:
                case ExpressionOp.SquareY: {
                    if (!TrySquare(arguments[0], out var cell)) { return false; }
                    var coordinate = cell.ToCoordinate();
                    value = ((operation == ExpressionOp.SquareX) ? coordinate.X : coordinate.Y);
                    return true;
                }
                case ExpressionOp.SquareRadius: {
                    if (!TrySquare(arguments[0], out var cell)) { return false; }
                    value = cell.Radius;
                    return true;
                }
                case ExpressionOp.SquareLength: {
                    if (!TrySquare(arguments[0], out var cell)) { return false; }
                    value = cell.ToCoordinate().Length;
                    return true;
                }
                case ExpressionOp.SquareEuclideanSquared: {
                    if (!TrySquare(arguments[0], out var cell)) { return false; }
                    value = cell.Norm;
                    return true;
                }
                case ExpressionOp.SquareDistance: {
                    if (!TrySquare(arguments[0], out var left) || !TrySquare(arguments[1], out var right)) { return false; }
                    value = Puck.Maths.SquareIndex.Distance(left: left, right: right);
                    return true;
                }
                case ExpressionOp.SquareChebyshev: {
                    if (!TrySquare(arguments[0], out var left) || !TrySquare(arguments[1], out var right)) { return false; }
                    var a = left.ToCoordinate();
                    var b = right.ToCoordinate();
                    value = Math.Max(val1: Math.Abs(value: (((long)a.X) - b.X)), val2: Math.Abs(value: (((long)a.Y) - b.Y)));
                    return true;
                }
                case ExpressionOp.SquareNeighbor: {
                    if (!TrySquare(arguments[0], out var cell)) { return false; }
                    value = cell.Neighbor(direction: ((int)arguments[1].FloorModulo(modulus: SquareCoordinate.NeighborCount))).Value;
                    return true;
                }
                case ExpressionOp.SquareRotate: {
                    if (!TrySquare(arguments[0], out var cell)) { return false; }
                    value = cell.Rotate(turns: ((int)arguments[1].FloorModulo(modulus: SquareCoordinate.NeighborCount))).Value;
                    return true;
                }
                case ExpressionOp.SquareMirror: {
                    if (!TrySquare(arguments[0], out var cell)) { return false; }
                    value = cell.Conjugate().Value;
                    return true;
                }
                case ExpressionOp.SquareSwap: {
                    if (!TrySquare(arguments[0], out var cell)) { return false; }
                    value = cell.Swap().Value;
                    return true;
                }
                case ExpressionOp.SquareAdd: {
                    if (!TrySquare(arguments[0], out var left) || !TrySquare(arguments[1], out var right)) { return false; }
                    value = (left + right).Value;
                    return true;
                }
                case ExpressionOp.SquareSubtract: {
                    if (!TrySquare(arguments[0], out var left) || !TrySquare(arguments[1], out var right)) { return false; }
                    value = (left - right).Value;
                    return true;
                }
                case ExpressionOp.SquareMultiply: {
                    if (!TrySquare(arguments[0], out var left) || !TrySquare(arguments[1], out var right)) { return false; }
                    value = (left * right).Value;
                    return true;
                }
                case ExpressionOp.SquareScale: {
                    if (!TrySquare(arguments[0], out var cell) || !IsInt(arguments[1])) { return false; }
                    value = cell.Scale(factor: ((int)arguments[1])).Value;
                    return true;
                }
                case ExpressionOp.SquareTranslate: {
                    if (!TrySquare(arguments[0], out var cell) || !IsInt(arguments[1]) || !IsInt(arguments[2])) { return false; }
                    value = cell.Translate(displacement: new SquareCoordinate(X: ((int)arguments[1]), Y: ((int)arguments[2]))).Value;
                    return true;
                }
                case ExpressionOp.GreatestCommonDivisor:
                    if ((arguments[0] == long.MinValue) || (arguments[1] == long.MinValue)) { return false; }
                    value = arguments[0].GreatestCommonDivisor(other: arguments[1]);
                    return true;
                case ExpressionOp.LeastCommonMultiple: {
                    if ((arguments[0] == long.MinValue) || (arguments[1] == long.MinValue)) { return false; }
                    var a = Math.Abs(value: arguments[0]);
                    var b = Math.Abs(value: arguments[1]);
                    if ((a == 0L) || (b == 0L)) { value = 0L; return true; }
                    var multiple = (((Int128)(a / a.GreatestCommonDivisor(other: b))) * b);
                    if (multiple > long.MaxValue) { return false; }
                    value = ((long)multiple);
                    return true;
                }
                case ExpressionOp.FloorModulo:
                    if ((arguments[1] == 0L) || ((arguments[1] == -1L) && (arguments[0] == long.MinValue))) { return false; }
                    value = arguments[0].FloorModulo(modulus: arguments[1]);
                    return true;
                case ExpressionOp.CycleForward:
                case ExpressionOp.CycleDistance: {
                    var modulus = arguments[2];
                    if (modulus <= 0L) { return false; }
                    var forward = unchecked(arguments[1] - arguments[0]).FloorModulo(modulus: modulus);
                    value = ((operation == ExpressionOp.CycleForward) ? forward : Math.Min(val1: forward, val2: (modulus - forward)));
                    return true;
                }
                case ExpressionOp.SmallestMissing:
                    value = System.Numerics.BitOperations.TrailingZeroCount(~((ulong)arguments[0]));
                    return true;
                case ExpressionOp.IsPrime:
                    value = (((arguments[0] >= 0L) && (((arguments[0] <= uint.MaxValue) ? ((uint)arguments[0]).IsPrime() : PrimeField64.IsPrime(value: ((ulong)arguments[0]))))) ? 1L : 0L);
                    return true;
                case ExpressionOp.Prime:
                    if ((arguments[0] < 0L) || (arguments[0] >= s_primes.Length)) { return false; }
                    value = s_primes[arguments[0]];
                    return true;
                case ExpressionOp.Choose:
                    if (!IsInt(arguments[0]) || !IsInt(arguments[1])) { return false; }
                    return TryLong(Combinatorics.Binomial(n: ((int)arguments[0]), k: ((int)arguments[1])), out value);
                case ExpressionOp.Factorial:
                    if (!IsInt(arguments[0])) { return false; }
                    return TryLong(Combinatorics.Factorial(n: ((int)arguments[0])), out value);
                case ExpressionOp.SubsetRank: {
                    var n = arguments[0];
                    var mask = arguments[1];
                    if ((n < 0L) || (n > MaskBits) || ((n < MaskBits) && ((((ulong)mask) >> ((int)n)) != 0UL))) { return false; }
                    Span<int> elements = stackalloc int[MaskBits];
                    var count = 0;
                    for (var remaining = (ulong)mask; remaining != 0UL; remaining &= (remaining - 1UL)) {
                        elements[count++] = System.Numerics.BitOperations.TrailingZeroCount(remaining);
                    }
                    return TryLong(Combinatorics.CombinationRank(n: ((int)n), combination: elements[..count]), out value);
                }
                case ExpressionOp.SubsetAt: {
                    var n = arguments[0];
                    var k = arguments[1];
                    if ((n < 0L) || (n > MaskBits) || (k < 0L) || (k > n) || (arguments[2] < 0L)) { return false; }
                    Span<int> elements = stackalloc int[MaskBits];
                    Combinatorics.CombinationUnrank(n: ((int)n), rank: ((ulong)arguments[2]), destination: elements[..((int)k)]);
                    var mask = 0UL;
                    for (var index = 0; index < k; index++) { mask |= (1UL << elements[index]); }
                    value = unchecked((long)mask);
                    return true;
                }
                case ExpressionOp.SubsetMember:
                    if (!IsInt(arguments[0]) || !IsInt(arguments[1]) || (arguments[2] < 0L) || !IsInt(arguments[3])) { return false; }
                    value = Combinatorics.CombinationElement(n: ((int)arguments[0]), k: ((int)arguments[1]), rank: ((ulong)arguments[2]), index: ((int)arguments[3]));
                    return true;
                case ExpressionOp.ArrangementRank: {
                    var n = arguments[0];
                    if ((n < 0L) || (n > NibbleSlots)) { return false; }
                    Span<int> elements = stackalloc int[NibbleSlots];
                    for (var index = 0; index < n; index++) { elements[index] = ((int)((((ulong)arguments[1]) >> (4 * index)) & 0xFUL)); }
                    return TryLong(Combinatorics.PermutationRank(permutation: elements[..((int)n)]), out value);
                }
                case ExpressionOp.ArrangementAt:
                case ExpressionOp.ArrangementMember: {
                    var n = arguments[0];
                    if ((n < 0L) || (n > NibbleSlots) || (arguments[1] < 0L)) { return false; }
                    Span<int> elements = stackalloc int[NibbleSlots];
                    Combinatorics.PermutationUnrank(rank: ((ulong)arguments[1]), destination: elements[..((int)n)]);
                    if (operation == ExpressionOp.ArrangementMember) {
                        var position = arguments[2];
                        if ((position < 0L) || (position >= n)) { return false; }
                        value = elements[(int)position];
                        return true;
                    }
                    var packed = 0UL;
                    for (var index = 0; index < n; index++) { packed |= (((ulong)elements[index]) << (4 * index)); }
                    value = unchecked((long)packed);
                    return true;
                }
                default:
                    return false;
            }
        } catch (OverflowException) {
            value = 0L;
            return false;
        } catch (ArgumentException) {
            value = 0L;
            return false;
        }
    }

    private static bool IsInt(long value) => ((value >= int.MinValue) && (value <= int.MaxValue));
    // A subset is a bitmask, so its universe is at most the word; a permutation is nibble-packed, so at most 16 slots.
    private const int MaskBits = 64;
    private const int NibbleSlots = 16;

    private static bool TryLong(ulong count, out long value) {
        value = unchecked((long)count);
        return (count <= long.MaxValue);
    }

    private static bool TrySquare(long value, out Puck.Maths.SquareIndex cell) {
        if ((value < 0L) || (value > Puck.Maths.SquareIndex.MaxValue)) {
            cell = default;
            return false;
        }
        cell = new Puck.Maths.SquareIndex(value: value);
        return true;
    }

    private static bool TryHex(long value, out HexagonalIndex cell) {
        if ((value < 0L) || (value > HexagonalIndex.MaxValue)) {
            cell = default;
            return false;
        }
        cell = new HexagonalIndex(value: value);
        return true;
    }
}
