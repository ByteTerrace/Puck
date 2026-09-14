namespace Puck.State;

// One description of each context-free operator. Literal/state payloads and topology-bound board queries keep
// their specialized lowering. Token factories are statically rooted, including in Native AOT builds.
internal enum ExpressionSignature : byte { Numeric, Int, Fixed, Comparison, Sign, Select }
internal sealed record ExpressionOperator(ExpressionOp Operation, int Arity, ExpressionSignature Signature,
    long Cost, string? Name, string? Symbol, bool Function, Type TokenType, Func<ValueToken> Make) {
    internal bool Admits(CellKind kind) => Signature switch {
        ExpressionSignature.Int => (kind == CellKind.Int),
        ExpressionSignature.Fixed => (kind == CellKind.Fixed),
        _ => (kind is CellKind.Int or CellKind.Fixed),
    };
}
internal static class ExpressionOperators {
    internal static int Arity(ExpressionOp operation) => operation switch {
        ExpressionOp.Constant or ExpressionOp.Operand => 0,
        ExpressionOp.BoardShift or ExpressionOp.BoardFill or ExpressionOp.BoardImage => 1,
        _ => Find(operation: operation)!.Arity,
    };
    internal static ValueToken Binary(string symbol) => Symbols[symbol].Make();
    internal static ExpressionOperator? Find(ExpressionOp operation) => ((((uint)operation) < ((uint)Operations.Length))
        ? Operations[((int)operation)]
        : null
    );
    internal static ExpressionOperator? Find(ValueToken token) => Tokens.GetValueOrDefault(key: token.GetType());

    private static ExpressionOperator?[] BuildOperations() {
        var result = new ExpressionOperator?[(Entries.Max(selector: static entry => ((int)entry.Operation)) + 1)];

        foreach (var entry in Entries) { result[((int)entry.Operation)] = entry; }
        return result;
    }
    private static ExpressionOperator Define<T>(ExpressionOp operation, int arity, ExpressionSignature signature,
        long cost, string? name, string? symbol, bool function) where T : ValueToken, new() =>
        new(
            operation,
            arity,
            signature,
            cost,
            name,
            symbol,
            function,
            typeof(T),
            static () => new T()
        );

    private static readonly ExpressionOperator[] Entries = [
        Define<ValueToken.Add>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.Add,
            signature: ExpressionSignature.Numeric,
            symbol: "+"
        ),
        Define<ValueToken.Subtract>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.Subtract,
            signature: ExpressionSignature.Numeric,
            symbol: "-"
        ),
        Define<ValueToken.Multiply>(
            arity: 2,
            cost: 3,
            function: false,
            name: null,
            operation: ExpressionOp.Multiply,
            signature: ExpressionSignature.Numeric,
            symbol: "*"
        ),
        Define<ValueToken.Divide>(
            arity: 2,
            cost: 16,
            function: false,
            name: null,
            operation: ExpressionOp.Divide,
            signature: ExpressionSignature.Numeric,
            symbol: "/"
        ),
        Define<ValueToken.Min>(
            arity: 2,
            cost: 2,
            function: false,
            name: "minimum",
            operation: ExpressionOp.Minimum,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define<ValueToken.Max>(
            arity: 2,
            cost: 2,
            function: false,
            name: "maximum",
            operation: ExpressionOp.Maximum,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define<ValueToken.Modulo>(
            arity: 2,
            cost: 16,
            function: false,
            name: null,
            operation: ExpressionOp.Modulo,
            signature: ExpressionSignature.Numeric,
            symbol: "%"
        ),
        Define<ValueToken.Clamp>(
            arity: 3,
            cost: 2,
            function: false,
            name: "clamp",
            operation: ExpressionOp.Clamp,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define<ValueToken.BitAnd>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.BitAnd,
            signature: ExpressionSignature.Int,
            symbol: "&"
        ),
        Define<ValueToken.BitOr>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.BitOr,
            signature: ExpressionSignature.Int,
            symbol: "|"
        ),
        Define<ValueToken.BitXor>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.BitXor,
            signature: ExpressionSignature.Int,
            symbol: "^"
        ),
        Define<ValueToken.ShiftLeft>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.ShiftLeft,
            signature: ExpressionSignature.Int,
            symbol: "<<"
        ),
        Define<ValueToken.ShiftRight>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.ShiftRight,
            signature: ExpressionSignature.Int,
            symbol: ">>"
        ),
        Define<ValueToken.ShiftRightLogical>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.ShiftRightLogical,
            signature: ExpressionSignature.Int,
            symbol: ">>>"
        ),
        Define<ValueToken.BitNot>(
            arity: 1,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.BitNot,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.Equal>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.Equal,
            signature: ExpressionSignature.Comparison,
            symbol: "=="
        ),
        Define<ValueToken.NotEqual>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.NotEqual,
            signature: ExpressionSignature.Comparison,
            symbol: "!="
        ),
        Define<ValueToken.Less>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.Less,
            signature: ExpressionSignature.Comparison,
            symbol: "<"
        ),
        Define<ValueToken.LessOrEqual>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.LessOrEqual,
            signature: ExpressionSignature.Comparison,
            symbol: "<="
        ),
        Define<ValueToken.Greater>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.Greater,
            signature: ExpressionSignature.Comparison,
            symbol: ">"
        ),
        Define<ValueToken.GreaterOrEqual>(
            arity: 2,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.GreaterOrEqual,
            signature: ExpressionSignature.Comparison,
            symbol: ">="
        ),
        Define<ValueToken.Select>(
            arity: 3,
            cost: 2,
            function: false,
            name: "select",
            operation: ExpressionOp.Select,
            signature: ExpressionSignature.Select,
            symbol: null
        ),
        Define<ValueToken.PopCount>(
            arity: 1,
            cost: 2,
            function: false,
            name: "setBitCount",
            operation: ExpressionOp.PopCount,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.LeadingZeroCount>(
            arity: 1,
            cost: 2,
            function: false,
            name: "leadingZeroCount",
            operation: ExpressionOp.LeadingZeroCount,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.TrailingZeroCount>(
            arity: 1,
            cost: 2,
            function: false,
            name: "trailingZeroCount",
            operation: ExpressionOp.TrailingZeroCount,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.LowestSetBit>(
            arity: 1,
            cost: 2,
            function: false,
            name: "lowestSetBit",
            operation: ExpressionOp.LowestSetBit,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.ClearLowestSetBit>(
            arity: 1,
            cost: 2,
            function: false,
            name: "clearLowestSetBit",
            operation: ExpressionOp.ClearLowestSetBit,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.ByteSwap>(
            arity: 1,
            cost: 2,
            function: false,
            name: "byteSwap",
            operation: ExpressionOp.ByteSwap,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.BitReverse>(
            arity: 1,
            cost: 2,
            function: false,
            name: "bitReverse",
            operation: ExpressionOp.BitReverse,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.ReplicationMask>(
            arity: 1,
            cost: 40,
            function: false,
            name: "replicationMask",
            operation: ExpressionOp.ReplicationMask,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.RepeatBits>(
            arity: 2,
            cost: 60,
            function: false,
            name: "repeatBits",
            operation: ExpressionOp.RepeatBits,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.RotateLeft>(
            arity: 2,
            cost: 3,
            function: false,
            name: "rotateLeft",
            operation: ExpressionOp.RotateLeft,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.RotateRight>(
            arity: 2,
            cost: 3,
            function: false,
            name: "rotateRight",
            operation: ExpressionOp.RotateRight,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.Negate>(
            arity: 1,
            cost: 1,
            function: false,
            name: null,
            operation: ExpressionOp.Negate,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define<ValueToken.Abs>(
            arity: 1,
            cost: 1,
            function: false,
            name: "absolute",
            operation: ExpressionOp.Abs,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define<ValueToken.Sign>(
            arity: 1,
            cost: 1,
            function: false,
            name: "sign",
            operation: ExpressionOp.Sign,
            signature: ExpressionSignature.Sign,
            symbol: null
        ),
        Define<ValueToken.ParallelBitExtract>(
            arity: 2,
            cost: 4,
            function: false,
            name: "parallelBitExtract",
            operation: ExpressionOp.ParallelBitExtract,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.ParallelBitDeposit>(
            arity: 2,
            cost: 4,
            function: false,
            name: "parallelBitDeposit",
            operation: ExpressionOp.ParallelBitDeposit,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.BitField>(
            arity: 3,
            cost: 3,
            function: false,
            name: "bitField",
            operation: ExpressionOp.BitField,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.BitInsert>(
            arity: 4,
            cost: 3,
            function: false,
            name: "bitInsert",
            operation: ExpressionOp.BitInsert,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.Pair>(
            arity: 2,
            cost: 12,
            function: true,
            name: "pair",
            operation: ExpressionOp.Pair,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.PairX>(
            arity: 1,
            cost: 25,
            function: true,
            name: "pairX",
            operation: ExpressionOp.PairX,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.PairY>(
            arity: 1,
            cost: 25,
            function: true,
            name: "pairY",
            operation: ExpressionOp.PairY,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.PairSwap>(
            arity: 1,
            cost: 25,
            function: true,
            name: "pairSwap",
            operation: ExpressionOp.PairSwap,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.PairMax>(
            arity: 1,
            cost: 25,
            function: true,
            name: "pairMaximum",
            operation: ExpressionOp.PairMax,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.PairMin>(
            arity: 1,
            cost: 25,
            function: true,
            name: "pairMinimum",
            operation: ExpressionOp.PairMin,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.PairSum>(
            arity: 1,
            cost: 25,
            function: true,
            name: "pairSum",
            operation: ExpressionOp.PairSum,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.PairDifference>(
            arity: 1,
            cost: 25,
            function: true,
            name: "pairDifference",
            operation: ExpressionOp.PairDifference,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.PairTranslate>(
            arity: 2,
            cost: 12,
            function: true,
            name: "pairTranslate",
            operation: ExpressionOp.PairTranslate,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.PairScale>(
            arity: 2,
            cost: 12,
            function: true,
            name: "pairScale",
            operation: ExpressionOp.PairScale,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.Morton>(
            arity: 2,
            cost: 15,
            function: true,
            name: "mortonIndex",
            operation: ExpressionOp.Morton,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.MortonX>(
            arity: 1,
            cost: 15,
            function: true,
            name: "mortonX",
            operation: ExpressionOp.MortonX,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.MortonY>(
            arity: 1,
            cost: 15,
            function: true,
            name: "mortonY",
            operation: ExpressionOp.MortonY,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.Hilbert>(
            arity: 3,
            cost: 60,
            function: true,
            name: "hilbertIndex",
            operation: ExpressionOp.Hilbert,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HilbertX>(
            arity: 2,
            cost: 60,
            function: true,
            name: "hilbertX",
            operation: ExpressionOp.HilbertX,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HilbertY>(
            arity: 2,
            cost: 60,
            function: true,
            name: "hilbertY",
            operation: ExpressionOp.HilbertY,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexIndex>(
            arity: 2,
            cost: 12,
            function: true,
            name: "hexIndex",
            operation: ExpressionOp.HexIndex,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexQ>(
            arity: 1,
            cost: 35,
            function: true,
            name: "hexQ",
            operation: ExpressionOp.HexQ,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexR>(
            arity: 1,
            cost: 35,
            function: true,
            name: "hexR",
            operation: ExpressionOp.HexR,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexRadius>(
            arity: 1,
            cost: 30,
            function: true,
            name: "hexRadius",
            operation: ExpressionOp.HexRadius,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexEuclideanSquared>(
            arity: 1,
            cost: 5,
            function: true,
            name: "hexEuclideanSquared",
            operation: ExpressionOp.HexEuclideanSquared,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexDistance>(
            arity: 2,
            cost: 10,
            function: true,
            name: "hexDistance",
            operation: ExpressionOp.HexDistance,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexNeighbor>(
            arity: 2,
            cost: 10,
            function: true,
            name: "hexNeighbor",
            operation: ExpressionOp.HexNeighbor,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexRotate>(
            arity: 2,
            cost: 10,
            function: true,
            name: "hexRotate",
            operation: ExpressionOp.HexRotate,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexMirror>(
            arity: 1,
            cost: 10,
            function: true,
            name: "hexMirror",
            operation: ExpressionOp.HexMirror,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexSwap>(
            arity: 1,
            cost: 10,
            function: true,
            name: "hexSwap",
            operation: ExpressionOp.HexSwap,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexAdd>(
            arity: 2,
            cost: 10,
            function: true,
            name: "hexAdd",
            operation: ExpressionOp.HexAdd,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexSubtract>(
            arity: 2,
            cost: 10,
            function: true,
            name: "hexSubtract",
            operation: ExpressionOp.HexSubtract,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexMultiply>(
            arity: 2,
            cost: 12,
            function: true,
            name: "hexMultiply",
            operation: ExpressionOp.HexMultiply,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexScale>(
            arity: 2,
            cost: 12,
            function: true,
            name: "hexScale",
            operation: ExpressionOp.HexScale,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.HexTranslate>(
            arity: 3,
            cost: 12,
            function: true,
            name: "hexTranslate",
            operation: ExpressionOp.HexTranslate,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.Layer>(
            arity: 4,
            cost: 20,
            function: true,
            name: "layer",
            operation: ExpressionOp.Layer,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.LayerOffset>(
            arity: 4,
            cost: 20,
            function: true,
            name: "layerOffset",
            operation: ExpressionOp.LayerOffset,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.LayerStart>(
            arity: 4,
            cost: 20,
            function: true,
            name: "layerStart",
            operation: ExpressionOp.LayerStart,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.LayerSize>(
            arity: 4,
            cost: 20,
            function: true,
            name: "layerSize",
            operation: ExpressionOp.LayerSize,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareRoot>(
            arity: 1,
            cost: 20,
            function: true,
            name: "squareRoot",
            operation: ExpressionOp.SquareRoot,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define<ValueToken.Sine>(
            arity: 1,
            cost: 25,
            function: true,
            name: "sine",
            operation: ExpressionOp.Sine,
            signature: ExpressionSignature.Fixed,
            symbol: null
        ),
        Define<ValueToken.Cosine>(
            arity: 1,
            cost: 25,
            function: true,
            name: "cosine",
            operation: ExpressionOp.Cosine,
            signature: ExpressionSignature.Fixed,
            symbol: null
        ),
        Define<ValueToken.SquareIndex>(
            arity: 2,
            cost: 10,
            function: true,
            name: "square",
            operation: ExpressionOp.SquareIndex,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareX>(
            arity: 1,
            cost: 25,
            function: true,
            name: "squareX",
            operation: ExpressionOp.SquareX,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareY>(
            arity: 1,
            cost: 25,
            function: true,
            name: "squareY",
            operation: ExpressionOp.SquareY,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareRadius>(
            arity: 1,
            cost: 5,
            function: true,
            name: "squareRadius",
            operation: ExpressionOp.SquareRadius,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareLength>(
            arity: 1,
            cost: 5,
            function: true,
            name: "squareLength",
            operation: ExpressionOp.SquareLength,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareEuclideanSquared>(
            arity: 1,
            cost: 5,
            function: true,
            name: "squareEuclideanSquared",
            operation: ExpressionOp.SquareEuclideanSquared,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareDistance>(
            arity: 2,
            cost: 6,
            function: true,
            name: "squareDistance",
            operation: ExpressionOp.SquareDistance,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareChebyshev>(
            arity: 2,
            cost: 6,
            function: true,
            name: "squareChebyshev",
            operation: ExpressionOp.SquareChebyshev,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareNeighbor>(
            arity: 2,
            cost: 10,
            function: true,
            name: "squareNeighbor",
            operation: ExpressionOp.SquareNeighbor,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareRotate>(
            arity: 2,
            cost: 10,
            function: true,
            name: "squareRotate",
            operation: ExpressionOp.SquareRotate,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareMirror>(
            arity: 1,
            cost: 10,
            function: true,
            name: "squareMirror",
            operation: ExpressionOp.SquareMirror,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareSwap>(
            arity: 1,
            cost: 10,
            function: true,
            name: "squareSwap",
            operation: ExpressionOp.SquareSwap,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareAdd>(
            arity: 2,
            cost: 10,
            function: true,
            name: "squareAdd",
            operation: ExpressionOp.SquareAdd,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareSubtract>(
            arity: 2,
            cost: 10,
            function: true,
            name: "squareSubtract",
            operation: ExpressionOp.SquareSubtract,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareMultiply>(
            arity: 2,
            cost: 10,
            function: true,
            name: "squareMultiply",
            operation: ExpressionOp.SquareMultiply,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareScale>(
            arity: 2,
            cost: 10,
            function: true,
            name: "squareScale",
            operation: ExpressionOp.SquareScale,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SquareTranslate>(
            arity: 3,
            cost: 10,
            function: true,
            name: "squareTranslate",
            operation: ExpressionOp.SquareTranslate,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.GreatestCommonDivisor>(
            arity: 2,
            cost: 40,
            function: true,
            name: "greatestCommonDivisor",
            operation: ExpressionOp.GreatestCommonDivisor,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.LeastCommonMultiple>(
            arity: 2,
            cost: 45,
            function: true,
            name: "leastCommonMultiple",
            operation: ExpressionOp.LeastCommonMultiple,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.FloorModulo>(
            arity: 2,
            cost: 18,
            function: true,
            name: "remainder",
            operation: ExpressionOp.FloorModulo,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.CycleForward>(
            arity: 3,
            cost: 18,
            function: true,
            name: "cycleForward",
            operation: ExpressionOp.CycleForward,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.CycleDistance>(
            arity: 3,
            cost: 18,
            function: true,
            name: "cycleDistance",
            operation: ExpressionOp.CycleDistance,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SmallestMissing>(
            arity: 1,
            cost: 2,
            function: true,
            name: "smallestMissing",
            operation: ExpressionOp.SmallestMissing,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.IsPrime>(
            arity: 1,
            cost: 200,
            function: true,
            name: "isPrime",
            operation: ExpressionOp.IsPrime,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.Prime>(
            arity: 1,
            cost: 3,
            function: true,
            name: "primeAt",
            operation: ExpressionOp.Prime,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.Choose>(
            arity: 2,
            cost: 40,
            function: true,
            name: "binomialCoefficient",
            operation: ExpressionOp.Choose,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.Factorial>(
            arity: 1,
            cost: 3,
            function: true,
            name: "factorial",
            operation: ExpressionOp.Factorial,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SubsetRank>(
            arity: 2,
            cost: 60,
            function: true,
            name: "subsetRank",
            operation: ExpressionOp.SubsetRank,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SubsetAt>(
            arity: 3,
            cost: 60,
            function: true,
            name: "subsetAt",
            operation: ExpressionOp.SubsetAt,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.SubsetMember>(
            arity: 4,
            cost: 60,
            function: true,
            name: "subsetMember",
            operation: ExpressionOp.SubsetMember,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.ArrangementRank>(
            arity: 2,
            cost: 60,
            function: true,
            name: "arrangementRank",
            operation: ExpressionOp.ArrangementRank,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.ArrangementAt>(
            arity: 2,
            cost: 60,
            function: true,
            name: "arrangementAt",
            operation: ExpressionOp.ArrangementAt,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.ArrangementMember>(
            arity: 3,
            cost: 60,
            function: true,
            name: "arrangementMember",
            operation: ExpressionOp.ArrangementMember,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define<ValueToken.Floor>(
            arity: 1,
            cost: 2,
            function: true,
            name: "floor",
            operation: ExpressionOp.Floor,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define<ValueToken.Ceiling>(
            arity: 1,
            cost: 2,
            function: true,
            name: "ceiling",
            operation: ExpressionOp.Ceiling,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define<ValueToken.Round>(
            arity: 1,
            cost: 2,
            function: true,
            name: "round",
            operation: ExpressionOp.Round,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
    ];
    private static readonly Dictionary<Type, ExpressionOperator> Tokens = Entries.ToDictionary(keySelector: static entry => entry.TokenType);

    internal static readonly IReadOnlyDictionary<string, ExpressionOperator> Calls = Entries.Where(predicate: static entry => (entry.Name is not null)).ToDictionary(
        static entry => entry.Name!,
        StringComparer.Ordinal
    );

    private static readonly Dictionary<string, ExpressionOperator> Symbols = Entries.Where(predicate: static entry => (entry.Symbol is not null)).ToDictionary(
        static entry => entry.Symbol!,
        StringComparer.Ordinal
    );
    private static readonly ExpressionOperator?[] Operations = BuildOperations();
}
