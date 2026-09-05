using Puck.Maths;

namespace Puck.State;

public static partial class ExpressionArithmetic {
    // Szudzik's pairing z·(z+1)+… with z = max(x, y) stays inside a signed 64-bit cell while z ≤ this bound.
    private const long MaxPairComponent = 3_037_000_498L;
    private const long MaxMortonComponent = ((1L << 31) - 1L);
    private const int MaxHilbertOrder = 31;
    private const int HexDirections = HexagonalCoordinate.NeighborCount;

    /// <summary>Returns how many values a function token consumes, or 0 for an operator token the arithmetic core
    /// evaluates itself.</summary>
    /// <param name="operation">The operation.</param>
    public static int FunctionArity(ExpressionOp operation) => operation switch {
        ExpressionOp.PairX or ExpressionOp.PairY or ExpressionOp.PairSwap or ExpressionOp.PairMax or ExpressionOp.PairMin or ExpressionOp.PairSum or ExpressionOp.PairDifference
            or ExpressionOp.MortonX or ExpressionOp.MortonY
            or ExpressionOp.HexQ or ExpressionOp.HexR or ExpressionOp.HexRadius or ExpressionOp.HexNorm or ExpressionOp.HexConjugate or ExpressionOp.HexSwap
            or ExpressionOp.SquareRoot or ExpressionOp.Sine or ExpressionOp.Cosine => 1,
        ExpressionOp.Pair or ExpressionOp.PairTranslate or ExpressionOp.PairScale or ExpressionOp.Morton or ExpressionOp.HilbertX or ExpressionOp.HilbertY
            or ExpressionOp.HexIndex or ExpressionOp.HexDistance or ExpressionOp.HexNeighbor or ExpressionOp.HexRotate or ExpressionOp.HexAdd or ExpressionOp.HexSubtract or ExpressionOp.HexMultiply or ExpressionOp.HexScale => 2,
        ExpressionOp.Hilbert or ExpressionOp.HexTranslate => 3,
        ExpressionOp.Layer or ExpressionOp.LayerOffset or ExpressionOp.LayerStart or ExpressionOp.LayerSize => 4,
        _ => 0,
    };

    /// <summary>Gets a value indicating whether a function token is admitted in expressions of <paramref name="kind"/>:
    /// <c>sqrt</c> in both kinds, <c>sin</c>/<c>cos</c> in fixed only, every other function in int only.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="kind">The expression kind.</param>
    public static bool FunctionAdmits(ExpressionOp operation, CellKind kind) => operation switch {
        ExpressionOp.SquareRoot => kind is CellKind.Int or CellKind.Fixed,
        ExpressionOp.Sine or ExpressionOp.Cosine => kind == CellKind.Fixed,
        _ => kind == CellKind.Int,
    };

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

        try {
            switch (operation) {
                case ExpressionOp.SquareRoot:
                    if (arguments[0] < 0L) { return false; }
                    value = ((kind == CellKind.Fixed)
                        ? FixedQ4816.Sqrt(value: FixedQ4816.FromRawBits(value: arguments[0])).Value
                        : ((long)((ulong)arguments[0]).SquareRoot()));
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
                case ExpressionOp.HexNorm: {
                    if (!TryHex(arguments[0], out var cell)) { return false; }
                    value = cell.Norm;
                    return true;
                }
                case ExpressionOp.HexConjugate: {
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

    private static bool TryHex(long value, out HexagonalIndex cell) {
        if ((value < 0L) || (value > HexagonalIndex.MaxValue)) {
            cell = default;
            return false;
        }
        cell = new HexagonalIndex(value: value);
        return true;
    }
}
