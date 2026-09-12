namespace Puck.State;

// One description of each context-free operator. Literal/state payloads and topology-bound board queries keep
// their specialized lowering. Token factories are statically rooted, including in Native AOT builds.
internal enum ExpressionSignature : byte { Numeric, Int, Fixed, Comparison, Sign, Select }

internal sealed record ExpressionOperator(ExpressionOp Operation, int Arity, ExpressionSignature Signature,
    long Cost, string? Name, string? Symbol, bool Function, Type TokenType, Func<ValueToken> Make) {
    internal bool Admits(CellKind kind) => Signature switch {
        ExpressionSignature.Int => kind == CellKind.Int,
        ExpressionSignature.Fixed => kind == CellKind.Fixed,
        _ => kind is CellKind.Int or CellKind.Fixed,
    };
}

internal static class ExpressionOperators {
    private static ExpressionOperator Define<T>(ExpressionOp operation, int arity, ExpressionSignature signature,
        long cost, string? name, string? symbol, bool function) where T : ValueToken, new() =>
        new(operation, arity, signature, cost, name, symbol, function, typeof(T), static () => new T());

    private static readonly ExpressionOperator[] Entries = [
        Define<ValueToken.Add>(ExpressionOp.Add, 2, ExpressionSignature.Numeric, 1, null, "+", false),
        Define<ValueToken.Subtract>(ExpressionOp.Subtract, 2, ExpressionSignature.Numeric, 1, null, "-", false),
        Define<ValueToken.Multiply>(ExpressionOp.Multiply, 2, ExpressionSignature.Numeric, 3, null, "*", false),
        Define<ValueToken.Divide>(ExpressionOp.Divide, 2, ExpressionSignature.Numeric, 16, null, "/", false),
        Define<ValueToken.Min>(ExpressionOp.Minimum, 2, ExpressionSignature.Numeric, 2, "minimum", null, false),
        Define<ValueToken.Max>(ExpressionOp.Maximum, 2, ExpressionSignature.Numeric, 2, "maximum", null, false),
        Define<ValueToken.Modulo>(ExpressionOp.Modulo, 2, ExpressionSignature.Numeric, 16, null, "%", false),
        Define<ValueToken.Clamp>(ExpressionOp.Clamp, 3, ExpressionSignature.Numeric, 2, "clamp", null, false),
        Define<ValueToken.BitAnd>(ExpressionOp.BitAnd, 2, ExpressionSignature.Int, 1, null, "&", false),
        Define<ValueToken.BitOr>(ExpressionOp.BitOr, 2, ExpressionSignature.Int, 1, null, "|", false),
        Define<ValueToken.BitXor>(ExpressionOp.BitXor, 2, ExpressionSignature.Int, 1, null, "^", false),
        Define<ValueToken.ShiftLeft>(ExpressionOp.ShiftLeft, 2, ExpressionSignature.Int, 1, null, "<<", false),
        Define<ValueToken.ShiftRight>(ExpressionOp.ShiftRight, 2, ExpressionSignature.Int, 1, null, ">>", false),
        Define<ValueToken.ShiftRightLogical>(ExpressionOp.ShiftRightLogical, 2, ExpressionSignature.Int, 1, null, ">>>", false),
        Define<ValueToken.BitNot>(ExpressionOp.BitNot, 1, ExpressionSignature.Int, 1, null, null, false),
        Define<ValueToken.Equal>(ExpressionOp.Equal, 2, ExpressionSignature.Comparison, 1, null, "==", false),
        Define<ValueToken.NotEqual>(ExpressionOp.NotEqual, 2, ExpressionSignature.Comparison, 1, null, "!=", false),
        Define<ValueToken.Less>(ExpressionOp.Less, 2, ExpressionSignature.Comparison, 1, null, "<", false),
        Define<ValueToken.LessOrEqual>(ExpressionOp.LessOrEqual, 2, ExpressionSignature.Comparison, 1, null, "<=", false),
        Define<ValueToken.Greater>(ExpressionOp.Greater, 2, ExpressionSignature.Comparison, 1, null, ">", false),
        Define<ValueToken.GreaterOrEqual>(ExpressionOp.GreaterOrEqual, 2, ExpressionSignature.Comparison, 1, null, ">=", false),
        Define<ValueToken.Select>(ExpressionOp.Select, 3, ExpressionSignature.Select, 2, "select", null, false),
        Define<ValueToken.PopCount>(ExpressionOp.PopCount, 1, ExpressionSignature.Int, 2, "setBitCount", null, false),
        Define<ValueToken.LeadingZeroCount>(ExpressionOp.LeadingZeroCount, 1, ExpressionSignature.Int, 2, "leadingZeroCount", null, false),
        Define<ValueToken.TrailingZeroCount>(ExpressionOp.TrailingZeroCount, 1, ExpressionSignature.Int, 2, "trailingZeroCount", null, false),
        Define<ValueToken.LowestSetBit>(ExpressionOp.LowestSetBit, 1, ExpressionSignature.Int, 2, "lowestSetBit", null, false),
        Define<ValueToken.ClearLowestSetBit>(ExpressionOp.ClearLowestSetBit, 1, ExpressionSignature.Int, 2, "clearLowestSetBit", null, false),
        Define<ValueToken.ByteSwap>(ExpressionOp.ByteSwap, 1, ExpressionSignature.Int, 2, "byteSwap", null, false),
        Define<ValueToken.BitReverse>(ExpressionOp.BitReverse, 1, ExpressionSignature.Int, 2, "bitReverse", null, false),
        Define<ValueToken.ReplicationMask>(ExpressionOp.ReplicationMask, 1, ExpressionSignature.Int, 40, "replicationMask", null, false),
        Define<ValueToken.RepeatBits>(ExpressionOp.RepeatBits, 2, ExpressionSignature.Int, 60, "repeatBits", null, false),
        Define<ValueToken.RotateLeft>(ExpressionOp.RotateLeft, 2, ExpressionSignature.Int, 3, "rotateLeft", null, false),
        Define<ValueToken.RotateRight>(ExpressionOp.RotateRight, 2, ExpressionSignature.Int, 3, "rotateRight", null, false),
        Define<ValueToken.Negate>(ExpressionOp.Negate, 1, ExpressionSignature.Numeric, 1, null, null, false),
        Define<ValueToken.Abs>(ExpressionOp.Abs, 1, ExpressionSignature.Numeric, 1, "absolute", null, false),
        Define<ValueToken.Sign>(ExpressionOp.Sign, 1, ExpressionSignature.Sign, 1, "sign", null, false),
        Define<ValueToken.ParallelBitExtract>(ExpressionOp.ParallelBitExtract, 2, ExpressionSignature.Int, 4, "parallelBitExtract", null, false),
        Define<ValueToken.ParallelBitDeposit>(ExpressionOp.ParallelBitDeposit, 2, ExpressionSignature.Int, 4, "parallelBitDeposit", null, false),
        Define<ValueToken.BitField>(ExpressionOp.BitField, 3, ExpressionSignature.Int, 3, "bitField", null, false),
        Define<ValueToken.BitInsert>(ExpressionOp.BitInsert, 4, ExpressionSignature.Int, 3, "bitInsert", null, false),
        Define<ValueToken.Pair>(ExpressionOp.Pair, 2, ExpressionSignature.Int, 12, "pair", null, true),
        Define<ValueToken.PairX>(ExpressionOp.PairX, 1, ExpressionSignature.Int, 25, "pairX", null, true),
        Define<ValueToken.PairY>(ExpressionOp.PairY, 1, ExpressionSignature.Int, 25, "pairY", null, true),
        Define<ValueToken.PairSwap>(ExpressionOp.PairSwap, 1, ExpressionSignature.Int, 25, "pairSwap", null, true),
        Define<ValueToken.PairMax>(ExpressionOp.PairMax, 1, ExpressionSignature.Int, 25, "pairMaximum", null, true),
        Define<ValueToken.PairMin>(ExpressionOp.PairMin, 1, ExpressionSignature.Int, 25, "pairMinimum", null, true),
        Define<ValueToken.PairSum>(ExpressionOp.PairSum, 1, ExpressionSignature.Int, 25, "pairSum", null, true),
        Define<ValueToken.PairDifference>(ExpressionOp.PairDifference, 1, ExpressionSignature.Int, 25, "pairDifference", null, true),
        Define<ValueToken.PairTranslate>(ExpressionOp.PairTranslate, 2, ExpressionSignature.Int, 12, "pairTranslate", null, true),
        Define<ValueToken.PairScale>(ExpressionOp.PairScale, 2, ExpressionSignature.Int, 12, "pairScale", null, true),
        Define<ValueToken.Morton>(ExpressionOp.Morton, 2, ExpressionSignature.Int, 15, "mortonIndex", null, true),
        Define<ValueToken.MortonX>(ExpressionOp.MortonX, 1, ExpressionSignature.Int, 15, "mortonX", null, true),
        Define<ValueToken.MortonY>(ExpressionOp.MortonY, 1, ExpressionSignature.Int, 15, "mortonY", null, true),
        Define<ValueToken.Hilbert>(ExpressionOp.Hilbert, 3, ExpressionSignature.Int, 60, "hilbertIndex", null, true),
        Define<ValueToken.HilbertX>(ExpressionOp.HilbertX, 2, ExpressionSignature.Int, 60, "hilbertX", null, true),
        Define<ValueToken.HilbertY>(ExpressionOp.HilbertY, 2, ExpressionSignature.Int, 60, "hilbertY", null, true),
        Define<ValueToken.HexIndex>(ExpressionOp.HexIndex, 2, ExpressionSignature.Int, 12, "hexIndex", null, true),
        Define<ValueToken.HexQ>(ExpressionOp.HexQ, 1, ExpressionSignature.Int, 35, "hexQ", null, true),
        Define<ValueToken.HexR>(ExpressionOp.HexR, 1, ExpressionSignature.Int, 35, "hexR", null, true),
        Define<ValueToken.HexRadius>(ExpressionOp.HexRadius, 1, ExpressionSignature.Int, 30, "hexRadius", null, true),
        Define<ValueToken.HexEuclideanSquared>(ExpressionOp.HexEuclideanSquared, 1, ExpressionSignature.Int, 5, "hexEuclideanSquared", null, true),
        Define<ValueToken.HexDistance>(ExpressionOp.HexDistance, 2, ExpressionSignature.Int, 10, "hexDistance", null, true),
        Define<ValueToken.HexNeighbor>(ExpressionOp.HexNeighbor, 2, ExpressionSignature.Int, 10, "hexNeighbor", null, true),
        Define<ValueToken.HexRotate>(ExpressionOp.HexRotate, 2, ExpressionSignature.Int, 10, "hexRotate", null, true),
        Define<ValueToken.HexMirror>(ExpressionOp.HexMirror, 1, ExpressionSignature.Int, 10, "hexMirror", null, true),
        Define<ValueToken.HexSwap>(ExpressionOp.HexSwap, 1, ExpressionSignature.Int, 10, "hexSwap", null, true),
        Define<ValueToken.HexAdd>(ExpressionOp.HexAdd, 2, ExpressionSignature.Int, 10, "hexAdd", null, true),
        Define<ValueToken.HexSubtract>(ExpressionOp.HexSubtract, 2, ExpressionSignature.Int, 10, "hexSubtract", null, true),
        Define<ValueToken.HexMultiply>(ExpressionOp.HexMultiply, 2, ExpressionSignature.Int, 12, "hexMultiply", null, true),
        Define<ValueToken.HexScale>(ExpressionOp.HexScale, 2, ExpressionSignature.Int, 12, "hexScale", null, true),
        Define<ValueToken.HexTranslate>(ExpressionOp.HexTranslate, 3, ExpressionSignature.Int, 12, "hexTranslate", null, true),
        Define<ValueToken.Layer>(ExpressionOp.Layer, 4, ExpressionSignature.Int, 20, "layer", null, true),
        Define<ValueToken.LayerOffset>(ExpressionOp.LayerOffset, 4, ExpressionSignature.Int, 20, "layerOffset", null, true),
        Define<ValueToken.LayerStart>(ExpressionOp.LayerStart, 4, ExpressionSignature.Int, 20, "layerStart", null, true),
        Define<ValueToken.LayerSize>(ExpressionOp.LayerSize, 4, ExpressionSignature.Int, 20, "layerSize", null, true),
        Define<ValueToken.SquareRoot>(ExpressionOp.SquareRoot, 1, ExpressionSignature.Numeric, 20, "squareRoot", null, true),
        Define<ValueToken.Sine>(ExpressionOp.Sine, 1, ExpressionSignature.Fixed, 25, "sine", null, true),
        Define<ValueToken.Cosine>(ExpressionOp.Cosine, 1, ExpressionSignature.Fixed, 25, "cosine", null, true),
        Define<ValueToken.SquareIndex>(ExpressionOp.SquareIndex, 2, ExpressionSignature.Int, 10, "square", null, true),
        Define<ValueToken.SquareX>(ExpressionOp.SquareX, 1, ExpressionSignature.Int, 25, "squareX", null, true),
        Define<ValueToken.SquareY>(ExpressionOp.SquareY, 1, ExpressionSignature.Int, 25, "squareY", null, true),
        Define<ValueToken.SquareRadius>(ExpressionOp.SquareRadius, 1, ExpressionSignature.Int, 5, "squareRadius", null, true),
        Define<ValueToken.SquareLength>(ExpressionOp.SquareLength, 1, ExpressionSignature.Int, 5, "squareLength", null, true),
        Define<ValueToken.SquareEuclideanSquared>(ExpressionOp.SquareEuclideanSquared, 1, ExpressionSignature.Int, 5, "squareEuclideanSquared", null, true),
        Define<ValueToken.SquareDistance>(ExpressionOp.SquareDistance, 2, ExpressionSignature.Int, 6, "squareDistance", null, true),
        Define<ValueToken.SquareChebyshev>(ExpressionOp.SquareChebyshev, 2, ExpressionSignature.Int, 6, "squareChebyshev", null, true),
        Define<ValueToken.SquareNeighbor>(ExpressionOp.SquareNeighbor, 2, ExpressionSignature.Int, 10, "squareNeighbor", null, true),
        Define<ValueToken.SquareRotate>(ExpressionOp.SquareRotate, 2, ExpressionSignature.Int, 10, "squareRotate", null, true),
        Define<ValueToken.SquareMirror>(ExpressionOp.SquareMirror, 1, ExpressionSignature.Int, 10, "squareMirror", null, true),
        Define<ValueToken.SquareSwap>(ExpressionOp.SquareSwap, 1, ExpressionSignature.Int, 10, "squareSwap", null, true),
        Define<ValueToken.SquareAdd>(ExpressionOp.SquareAdd, 2, ExpressionSignature.Int, 10, "squareAdd", null, true),
        Define<ValueToken.SquareSubtract>(ExpressionOp.SquareSubtract, 2, ExpressionSignature.Int, 10, "squareSubtract", null, true),
        Define<ValueToken.SquareMultiply>(ExpressionOp.SquareMultiply, 2, ExpressionSignature.Int, 10, "squareMultiply", null, true),
        Define<ValueToken.SquareScale>(ExpressionOp.SquareScale, 2, ExpressionSignature.Int, 10, "squareScale", null, true),
        Define<ValueToken.SquareTranslate>(ExpressionOp.SquareTranslate, 3, ExpressionSignature.Int, 10, "squareTranslate", null, true),
        Define<ValueToken.GreatestCommonDivisor>(ExpressionOp.GreatestCommonDivisor, 2, ExpressionSignature.Int, 40, "greatestCommonDivisor", null, true),
        Define<ValueToken.LeastCommonMultiple>(ExpressionOp.LeastCommonMultiple, 2, ExpressionSignature.Int, 45, "leastCommonMultiple", null, true),
        Define<ValueToken.FloorModulo>(ExpressionOp.FloorModulo, 2, ExpressionSignature.Int, 18, "remainder", null, true),
        Define<ValueToken.CycleForward>(ExpressionOp.CycleForward, 3, ExpressionSignature.Int, 18, "cycleForward", null, true),
        Define<ValueToken.CycleDistance>(ExpressionOp.CycleDistance, 3, ExpressionSignature.Int, 18, "cycleDistance", null, true),
        Define<ValueToken.SmallestMissing>(ExpressionOp.SmallestMissing, 1, ExpressionSignature.Int, 2, "smallestMissing", null, true),
        Define<ValueToken.IsPrime>(ExpressionOp.IsPrime, 1, ExpressionSignature.Int, 200, "isPrime", null, true),
        Define<ValueToken.Prime>(ExpressionOp.Prime, 1, ExpressionSignature.Int, 3, "primeAt", null, true),
        Define<ValueToken.Choose>(ExpressionOp.Choose, 2, ExpressionSignature.Int, 40, "binomialCoefficient", null, true),
        Define<ValueToken.Factorial>(ExpressionOp.Factorial, 1, ExpressionSignature.Int, 3, "factorial", null, true),
        Define<ValueToken.SubsetRank>(ExpressionOp.SubsetRank, 2, ExpressionSignature.Int, 60, "subsetRank", null, true),
        Define<ValueToken.SubsetAt>(ExpressionOp.SubsetAt, 3, ExpressionSignature.Int, 60, "subsetAt", null, true),
        Define<ValueToken.SubsetMember>(ExpressionOp.SubsetMember, 4, ExpressionSignature.Int, 60, "subsetMember", null, true),
        Define<ValueToken.ArrangementRank>(ExpressionOp.ArrangementRank, 2, ExpressionSignature.Int, 60, "arrangementRank", null, true),
        Define<ValueToken.ArrangementAt>(ExpressionOp.ArrangementAt, 2, ExpressionSignature.Int, 60, "arrangementAt", null, true),
        Define<ValueToken.ArrangementMember>(ExpressionOp.ArrangementMember, 3, ExpressionSignature.Int, 60, "arrangementMember", null, true),
        Define<ValueToken.Floor>(ExpressionOp.Floor, 1, ExpressionSignature.Numeric, 2, "floor", null, true),
        Define<ValueToken.Ceiling>(ExpressionOp.Ceiling, 1, ExpressionSignature.Numeric, 2, "ceiling", null, true),
        Define<ValueToken.Round>(ExpressionOp.Round, 1, ExpressionSignature.Numeric, 2, "round", null, true),
    ];
    private static readonly Dictionary<Type, ExpressionOperator> Tokens = Entries.ToDictionary(static entry => entry.TokenType);
    internal static readonly IReadOnlyDictionary<string, ExpressionOperator> Calls = Entries.Where(static entry => entry.Name is not null).ToDictionary(static entry => entry.Name!, StringComparer.Ordinal);
    private static readonly Dictionary<string, ExpressionOperator> Symbols = Entries.Where(static entry => entry.Symbol is not null).ToDictionary(static entry => entry.Symbol!, StringComparer.Ordinal);
    private static readonly ExpressionOperator?[] Operations = BuildOperations();

    private static ExpressionOperator?[] BuildOperations() {
        var result = new ExpressionOperator?[Entries.Max(static entry => (int)entry.Operation) + 1];
        foreach (var entry in Entries) { result[(int)entry.Operation] = entry; }
        return result;
    }
    internal static ExpressionOperator? Find(ExpressionOp operation) => (uint)operation < (uint)Operations.Length ? Operations[(int)operation] : null;
    internal static ExpressionOperator? Find(ValueToken token) => Tokens.GetValueOrDefault(token.GetType());
    internal static ValueToken Binary(string symbol) => Symbols[symbol].Make();
    internal static int Arity(ExpressionOp operation) => operation switch {
        ExpressionOp.Constant or ExpressionOp.Operand => 0,
        ExpressionOp.BoardShift or ExpressionOp.BoardFill or ExpressionOp.BoardImage => 1,
        _ => Find(operation)!.Arity,
    };
}
