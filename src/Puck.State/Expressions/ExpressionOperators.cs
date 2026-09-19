namespace Puck.State;

/// <summary>The kind signature of an <see cref="ExpressionOp"/>: which numeric kinds it is defined over, and how it
/// shapes the stack.</summary>
public enum ExpressionSignature : byte {
    /// <summary>Defined over Int and Fixed; every operand and the result share the expression's kind.</summary>
    Numeric,
    /// <summary>Int only.</summary>
    Int,
    /// <summary>Fixed only.</summary>
    Fixed,
    /// <summary>Reads two operands of one kind and yields Int 1 or 0.</summary>
    Comparison,
    /// <summary>Reads one numeric operand and yields Int -1, 0, or 1.</summary>
    Sign,
    /// <summary>Reads an Int condition and two arms of one kind, and yields that kind.</summary>
    Select,
    /// <summary>Reads one operand that may be absent and yields Int 1 or 0.</summary>
    Absence,
    /// <summary>Reads an operand that may be absent and a fallback of the same kind, and yields that kind.</summary>
    Coalesce,
    /// <summary>Reduces a family through a subprogram; Count, All, and Any yield Int, Sum the family's kind.</summary>
    Fold,
    /// <summary>Consumes nothing from the stack and pushes what its payload addresses.</summary>
    Payload,
}
/// <summary>One row of the expression operator table: everything the parser, compiler, folder, printer, and
/// evaluator need to know about one <see cref="ExpressionOp"/>.</summary>
/// <param name="Operation">The operation this row describes.</param>
/// <param name="Arity">How many values the operation consumes from the stack.</param>
/// <param name="Signature">Which numeric kinds it is defined over, and how it shapes the stack.</param>
/// <param name="Cost">Its heuristic work units, before any payload-dependent scaling.</param>
/// <param name="Name">Its call spelling, or <see langword="null"/> when it has none.</param>
/// <param name="Symbol">Its operator spelling, or <see langword="null"/> when it has none.</param>
/// <param name="Function">Whether the operation evaluates through
/// <see cref="ExpressionArithmetic.TryValidatedFunction"/> rather than the unary or binary entry.</param>
/// <param name="Payload">Which <see cref="InstructionPayload"/> case its instruction must carry.</param>
public sealed record ExpressionOperator(ExpressionOp Operation, int Arity, ExpressionSignature Signature,
    long Cost, string? Name, string? Symbol, bool Function, PayloadShape Payload) {
    /// <summary>Returns a value indicating whether the operation is defined over a numeric kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns><see langword="true"/> when the operation admits it.</returns>
    public bool Admits(CellKind kind) => Signature switch {
        ExpressionSignature.Int => (kind == CellKind.Int),
        ExpressionSignature.Fixed => (kind == CellKind.Fixed),
        _ => (kind is CellKind.Int or CellKind.Fixed),
    };
}
/// <summary>The one description of each <see cref="ExpressionOp"/>. Every operation has exactly one row, so no
/// operation is described in two places.</summary>
public static class ExpressionOperators {
    /// <summary>Gets every row, in declaration order.</summary>
    public static IReadOnlyList<ExpressionOperator> All => Entries;
    /// <summary>Gets every row the infix parser admits as a call, keyed by its spelling. A fold, a board query, and
    /// a vector call carry a name but parse through their own arm, so they are absent here.</summary>
    public static IReadOnlyDictionary<string, ExpressionOperator> Calls { get; }

    /// <summary>Returns how many values an operation consumes from the stack.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns>The count; zero for one that pushes what its payload addresses.</returns>
    public static int Arity(ExpressionOp operation) => (Find(operation: operation)?.Arity ?? 0);
    /// <summary>Returns the instruction an operator symbol denotes.</summary>
    /// <param name="symbol">The symbol.</param>
    /// <returns>The instruction.</returns>
    public static Instruction Binary(string symbol) => Instruction.Of(operation: Symbols[symbol].Operation);
    /// <summary>Returns an operation's row.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns>The row, or <see langword="null"/> when the value is not a declared operation.</returns>
    public static ExpressionOperator? Find(ExpressionOp operation) => ((((uint)operation) < ((uint)Operations.Length))
        ? Operations[((int)operation)]
        : null
    );
    /// <summary>Returns an instruction's row.</summary>
    /// <param name="instruction">The instruction.</param>
    /// <returns>The row, or <see langword="null"/> when its operation is not declared.</returns>
    public static ExpressionOperator? Find(Instruction instruction) {
        ArgumentNullException.ThrowIfNull(argument: instruction);

        return Find(operation: instruction.Operation);
    }
    /// <summary>Returns which payload case an operation's instruction must carry.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns>The payload shape.</returns>
    public static PayloadShape PayloadOf(ExpressionOp operation) => (Find(operation: operation)?.Payload ?? PayloadShape.None);
    /// <summary>Returns the row an operator symbol denotes.</summary>
    /// <param name="symbol">The symbol.</param>
    /// <param name="descriptor">The row, on success.</param>
    /// <returns><see langword="true"/> when the symbol is one this table declares.</returns>
    public static bool TryFindSymbol(string symbol, out ExpressionOperator? descriptor) =>
        Symbols.TryGetValue(
            key: symbol,
            value: out descriptor
        );

    private static ExpressionOperator?[] BuildOperations() {
        var result = new ExpressionOperator?[(Entries.Max(selector: static entry => ((int)entry.Operation)) + 1)];

        foreach (var entry in Entries) { result[((int)entry.Operation)] = entry; }
        return result;
    }
    private static ExpressionOperator Define(ExpressionOp operation, int arity, ExpressionSignature signature,
        long cost, string? name, string? symbol, bool function, PayloadShape payload) =>
        new(
            operation,
            arity,
            signature,
            cost,
            name,
            symbol,
            function,
            payload
        );

    static ExpressionOperators() {
        Calls = Entries.Where(predicate: static entry => ((entry.Name is not null) && (entry.Signature is not (ExpressionSignature.Fold or ExpressionSignature.Payload)) && (entry.Payload is PayloadShape.None))).ToDictionary(
            static entry => entry.Name!,
            StringComparer.Ordinal
        );
    }

    private static readonly ExpressionOperator[] Entries = [
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.Add,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: "+"
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.Subtract,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: "-"
        ),
        Define(
            arity: 2,
            cost: 3L,
            function: false,
            name: null,
            operation: ExpressionOp.Multiply,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: "*"
        ),
        Define(
            arity: 2,
            cost: 16L,
            function: false,
            name: null,
            operation: ExpressionOp.Divide,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: "/"
        ),
        Define(
            arity: 2,
            cost: 2L,
            function: false,
            name: "minimum",
            operation: ExpressionOp.Minimum,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 2L,
            function: false,
            name: "maximum",
            operation: ExpressionOp.Maximum,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 16L,
            function: false,
            name: null,
            operation: ExpressionOp.Modulo,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: "%"
        ),
        Define(
            arity: 3,
            cost: 2L,
            function: false,
            name: "clamp",
            operation: ExpressionOp.Clamp,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.BitAnd,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: "&"
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.BitOr,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: "|"
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.BitXor,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: "^"
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.ShiftLeft,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: "<<"
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.ShiftRight,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: ">>"
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.ShiftRightLogical,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: ">>>"
        ),
        Define(
            arity: 1,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.BitNot,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.Equal,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Comparison,
            symbol: "=="
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.NotEqual,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Comparison,
            symbol: "!="
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.Less,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Comparison,
            symbol: "<"
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.LessOrEqual,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Comparison,
            symbol: "<="
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.Greater,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Comparison,
            symbol: ">"
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.GreaterOrEqual,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Comparison,
            symbol: ">="
        ),
        Define(
            arity: 3,
            cost: 2L,
            function: false,
            name: "select",
            operation: ExpressionOp.Select,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Select,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 2L,
            function: false,
            name: "setBitCount",
            operation: ExpressionOp.PopCount,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 2L,
            function: false,
            name: "leadingZeroCount",
            operation: ExpressionOp.LeadingZeroCount,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 2L,
            function: false,
            name: "trailingZeroCount",
            operation: ExpressionOp.TrailingZeroCount,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 2L,
            function: false,
            name: "lowestSetBit",
            operation: ExpressionOp.LowestSetBit,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 2L,
            function: false,
            name: "clearLowestSetBit",
            operation: ExpressionOp.ClearLowestSetBit,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 2L,
            function: false,
            name: "byteSwap",
            operation: ExpressionOp.ByteSwap,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 2L,
            function: false,
            name: "bitReverse",
            operation: ExpressionOp.BitReverse,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 40L,
            function: false,
            name: "replicationMask",
            operation: ExpressionOp.ReplicationMask,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 60L,
            function: false,
            name: "repeatBits",
            operation: ExpressionOp.RepeatBits,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 3L,
            function: false,
            name: "rotateLeft",
            operation: ExpressionOp.RotateLeft,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 3L,
            function: false,
            name: "rotateRight",
            operation: ExpressionOp.RotateRight,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.Negate,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 1L,
            function: false,
            name: "absolute",
            operation: ExpressionOp.Abs,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 1L,
            function: false,
            name: "sign",
            operation: ExpressionOp.Sign,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Sign,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 4L,
            function: false,
            name: "parallelBitExtract",
            operation: ExpressionOp.ParallelBitExtract,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 4L,
            function: false,
            name: "parallelBitDeposit",
            operation: ExpressionOp.ParallelBitDeposit,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 3,
            cost: 3L,
            function: false,
            name: "bitField",
            operation: ExpressionOp.BitField,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 4,
            cost: 3L,
            function: false,
            name: "bitInsert",
            operation: ExpressionOp.BitInsert,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 12L,
            function: true,
            name: "pair",
            operation: ExpressionOp.Pair,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 25L,
            function: true,
            name: "pairX",
            operation: ExpressionOp.PairX,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 25L,
            function: true,
            name: "pairY",
            operation: ExpressionOp.PairY,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 25L,
            function: true,
            name: "pairSwap",
            operation: ExpressionOp.PairSwap,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 25L,
            function: true,
            name: "pairMaximum",
            operation: ExpressionOp.PairMax,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 25L,
            function: true,
            name: "pairMinimum",
            operation: ExpressionOp.PairMin,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 25L,
            function: true,
            name: "pairSum",
            operation: ExpressionOp.PairSum,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 25L,
            function: true,
            name: "pairDifference",
            operation: ExpressionOp.PairDifference,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 12L,
            function: true,
            name: "pairTranslate",
            operation: ExpressionOp.PairTranslate,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 12L,
            function: true,
            name: "pairScale",
            operation: ExpressionOp.PairScale,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 15L,
            function: true,
            name: "mortonIndex",
            operation: ExpressionOp.Morton,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 15L,
            function: true,
            name: "mortonX",
            operation: ExpressionOp.MortonX,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 15L,
            function: true,
            name: "mortonY",
            operation: ExpressionOp.MortonY,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 3,
            cost: 60L,
            function: true,
            name: "hilbertIndex",
            operation: ExpressionOp.Hilbert,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 60L,
            function: true,
            name: "hilbertX",
            operation: ExpressionOp.HilbertX,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 60L,
            function: true,
            name: "hilbertY",
            operation: ExpressionOp.HilbertY,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 12L,
            function: true,
            name: "hexIndex",
            operation: ExpressionOp.HexIndex,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 35L,
            function: true,
            name: "hexQ",
            operation: ExpressionOp.HexQ,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 35L,
            function: true,
            name: "hexR",
            operation: ExpressionOp.HexR,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 30L,
            function: true,
            name: "hexRadius",
            operation: ExpressionOp.HexRadius,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 5L,
            function: true,
            name: "hexEuclideanSquared",
            operation: ExpressionOp.HexEuclideanSquared,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 10L,
            function: true,
            name: "hexDistance",
            operation: ExpressionOp.HexDistance,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 10L,
            function: true,
            name: "hexNeighbor",
            operation: ExpressionOp.HexNeighbor,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 10L,
            function: true,
            name: "hexRotate",
            operation: ExpressionOp.HexRotate,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 10L,
            function: true,
            name: "hexMirror",
            operation: ExpressionOp.HexMirror,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 10L,
            function: true,
            name: "hexSwap",
            operation: ExpressionOp.HexSwap,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 10L,
            function: true,
            name: "hexAdd",
            operation: ExpressionOp.HexAdd,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 10L,
            function: true,
            name: "hexSubtract",
            operation: ExpressionOp.HexSubtract,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 12L,
            function: true,
            name: "hexMultiply",
            operation: ExpressionOp.HexMultiply,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 12L,
            function: true,
            name: "hexScale",
            operation: ExpressionOp.HexScale,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 3,
            cost: 12L,
            function: true,
            name: "hexTranslate",
            operation: ExpressionOp.HexTranslate,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 4,
            cost: 20L,
            function: true,
            name: "layer",
            operation: ExpressionOp.Layer,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 4,
            cost: 20L,
            function: true,
            name: "layerOffset",
            operation: ExpressionOp.LayerOffset,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 4,
            cost: 20L,
            function: true,
            name: "layerStart",
            operation: ExpressionOp.LayerStart,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 4,
            cost: 20L,
            function: true,
            name: "layerSize",
            operation: ExpressionOp.LayerSize,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 20L,
            function: true,
            name: "squareRoot",
            operation: ExpressionOp.SquareRoot,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 25L,
            function: true,
            name: "sine",
            operation: ExpressionOp.Sine,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Fixed,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 25L,
            function: true,
            name: "cosine",
            operation: ExpressionOp.Cosine,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Fixed,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 10L,
            function: true,
            name: "square",
            operation: ExpressionOp.SquareIndex,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 25L,
            function: true,
            name: "squareX",
            operation: ExpressionOp.SquareX,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 25L,
            function: true,
            name: "squareY",
            operation: ExpressionOp.SquareY,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 5L,
            function: true,
            name: "squareRadius",
            operation: ExpressionOp.SquareRadius,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 5L,
            function: true,
            name: "squareLength",
            operation: ExpressionOp.SquareLength,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 5L,
            function: true,
            name: "squareEuclideanSquared",
            operation: ExpressionOp.SquareEuclideanSquared,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 6L,
            function: true,
            name: "squareDistance",
            operation: ExpressionOp.SquareDistance,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 6L,
            function: true,
            name: "squareChebyshev",
            operation: ExpressionOp.SquareChebyshev,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 10L,
            function: true,
            name: "squareNeighbor",
            operation: ExpressionOp.SquareNeighbor,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 10L,
            function: true,
            name: "squareRotate",
            operation: ExpressionOp.SquareRotate,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 10L,
            function: true,
            name: "squareMirror",
            operation: ExpressionOp.SquareMirror,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 10L,
            function: true,
            name: "squareSwap",
            operation: ExpressionOp.SquareSwap,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 10L,
            function: true,
            name: "squareAdd",
            operation: ExpressionOp.SquareAdd,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 10L,
            function: true,
            name: "squareSubtract",
            operation: ExpressionOp.SquareSubtract,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 10L,
            function: true,
            name: "squareMultiply",
            operation: ExpressionOp.SquareMultiply,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 10L,
            function: true,
            name: "squareScale",
            operation: ExpressionOp.SquareScale,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 3,
            cost: 10L,
            function: true,
            name: "squareTranslate",
            operation: ExpressionOp.SquareTranslate,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 40L,
            function: true,
            name: "greatestCommonDivisor",
            operation: ExpressionOp.GreatestCommonDivisor,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 45L,
            function: true,
            name: "leastCommonMultiple",
            operation: ExpressionOp.LeastCommonMultiple,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 18L,
            function: true,
            name: "remainder",
            operation: ExpressionOp.FloorModulo,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 3,
            cost: 18L,
            function: true,
            name: "cycleForward",
            operation: ExpressionOp.CycleForward,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 3,
            cost: 18L,
            function: true,
            name: "cycleDistance",
            operation: ExpressionOp.CycleDistance,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 2L,
            function: true,
            name: "smallestMissing",
            operation: ExpressionOp.SmallestMissing,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 200L,
            function: true,
            name: "isPrime",
            operation: ExpressionOp.IsPrime,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 3L,
            function: true,
            name: "primeAt",
            operation: ExpressionOp.Prime,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 40L,
            function: true,
            name: "binomialCoefficient",
            operation: ExpressionOp.Choose,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 3L,
            function: true,
            name: "factorial",
            operation: ExpressionOp.Factorial,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 60L,
            function: true,
            name: "subsetRank",
            operation: ExpressionOp.SubsetRank,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 3,
            cost: 60L,
            function: true,
            name: "subsetAt",
            operation: ExpressionOp.SubsetAt,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 4,
            cost: 60L,
            function: true,
            name: "subsetMember",
            operation: ExpressionOp.SubsetMember,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 60L,
            function: true,
            name: "arrangementRank",
            operation: ExpressionOp.ArrangementRank,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 60L,
            function: true,
            name: "arrangementAt",
            operation: ExpressionOp.ArrangementAt,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 3,
            cost: 60L,
            function: true,
            name: "arrangementMember",
            operation: ExpressionOp.ArrangementMember,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 2L,
            function: true,
            name: "floor",
            operation: ExpressionOp.Floor,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 2L,
            function: true,
            name: "ceiling",
            operation: ExpressionOp.Ceiling,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 2L,
            function: true,
            name: "round",
            operation: ExpressionOp.Round,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Numeric,
            symbol: null
        ),
        Define(
            arity: 0,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.Constant,
            payload: PayloadShape.Constant,
            signature: ExpressionSignature.Payload,
            symbol: null
        ),
        Define(
            arity: 0,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.Operand,
            payload: PayloadShape.State,
            signature: ExpressionSignature.Payload,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 24L,
            function: true,
            name: "boardShift",
            operation: ExpressionOp.BoardShift,
            payload: PayloadShape.Board,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 1536L,
            function: true,
            name: "boardFill",
            operation: ExpressionOp.BoardFill,
            payload: PayloadShape.Board,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 24L,
            function: true,
            name: "boardImage",
            operation: ExpressionOp.BoardImage,
            payload: PayloadShape.Board,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 0,
            cost: 2L,
            function: true,
            name: "dot",
            operation: ExpressionOp.Dot,
            payload: PayloadShape.Vector,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 0,
            cost: 2L,
            function: true,
            name: "similarity",
            operation: ExpressionOp.Similarity,
            payload: PayloadShape.Vector,
            signature: ExpressionSignature.Fixed,
            symbol: null
        ),
        Define(
            arity: 0,
            cost: 2L,
            function: true,
            name: "identical",
            operation: ExpressionOp.Identical,
            payload: PayloadShape.Vector,
            signature: ExpressionSignature.Int,
            symbol: null
        ),
        Define(
            arity: 1,
            cost: 1L,
            function: true,
            name: "isAbsent",
            operation: ExpressionOp.IsAbsent,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Absence,
            symbol: null
        ),
        Define(
            arity: 2,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.Coalesce,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Coalesce,
            symbol: "??"
        ),
        Define(
            arity: 0,
            cost: 1L,
            function: true,
            name: "all",
            operation: ExpressionOp.All,
            payload: PayloadShape.Fold,
            signature: ExpressionSignature.Fold,
            symbol: null
        ),
        Define(
            arity: 0,
            cost: 1L,
            function: true,
            name: "any",
            operation: ExpressionOp.Any,
            payload: PayloadShape.Fold,
            signature: ExpressionSignature.Fold,
            symbol: null
        ),
        Define(
            arity: 0,
            cost: 1L,
            function: true,
            name: "count",
            operation: ExpressionOp.Count,
            payload: PayloadShape.Fold,
            signature: ExpressionSignature.Fold,
            symbol: null
        ),
        Define(
            arity: 0,
            cost: 1L,
            function: true,
            name: "sum",
            operation: ExpressionOp.Sum,
            payload: PayloadShape.Fold,
            signature: ExpressionSignature.Fold,
            symbol: null
        ),
        Define(
            arity: 0,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.Call,
            payload: PayloadShape.Call,
            signature: ExpressionSignature.Payload,
            symbol: null
        ),
        Define(
            arity: 0,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.Argument,
            payload: PayloadShape.Argument,
            signature: ExpressionSignature.Payload,
            symbol: null
        ),
        Define(
            arity: 0,
            cost: 1L,
            function: false,
            name: null,
            operation: ExpressionOp.Member,
            payload: PayloadShape.None,
            signature: ExpressionSignature.Payload,
            symbol: null
        ),
    ];
    private static readonly Dictionary<string, ExpressionOperator> Symbols = Entries.Where(predicate: static entry => (entry.Symbol is not null)).ToDictionary(
        static entry => entry.Symbol!,
        StringComparer.Ordinal
    );
    private static readonly ExpressionOperator?[] Operations = BuildOperations();
}
