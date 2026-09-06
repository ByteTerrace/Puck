namespace Puck.State;

public static partial class RuleCompiler {
    /// <summary>Compiles a postfix value expression into its evaluated token program, proving it with a typed stack:
    /// every slot's kind is known at compile time, so an Int comparison result can feed a select inside a Fixed
    /// expression while never reaching an arithmetic operator of the wrong kind. The program leaves exactly one value
    /// of <paramref name="kind"/>.</summary>
    /// <param name="expression">The authored expression.</param>
    /// <param name="kind">The kind the expression must produce — the destination cell's, or the binding's.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    /// <param name="context">The compile context.</param>
    /// <exception cref="RuleException">The expression is missing, over-long, ill-typed, or names something the
    /// section does not declare.</exception>
    public static CompiledExpressionToken[] CompileExpression(ValueExpression? expression, CellKind kind, string ruleName, string verb, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (expression is null) {
            throw new RuleException(refusal: RuleRefusal.EffectSourceAmbiguous, ruleName: ruleName, detail: $"'{verb}' carries a null expression");
        }
        if (expression.Tokens is not { Count: > 0 } authored || (authored.Count > RuleCapacity.MaxExpressionTokens)) {
            throw new RuleException(
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName,
                detail: $"'{verb}' expression must carry 1..{RuleCapacity.MaxExpressionTokens} postfix tokens"
            );
        }

        var tokens = new CompiledExpressionToken[authored.Count];
        var kinds = new CellKind[authored.Count];
        var depth = 0;

        for (var index = 0; index < authored.Count; index++) {
            tokens[index] = authored[index] switch {
                ValueToken.Constant constant => Push(new CompiledExpressionToken(
                        Operation: ExpressionOp.Constant,
                        Constant: LiteralToRaw(kind: kind, literal: constant.Value, ruleName: ruleName, verb: verb)
                    ), kind),
                ValueToken.State state => ResolveState(state),
                ValueToken.Add => Binary(ExpressionOp.Add),
                ValueToken.Subtract => Binary(ExpressionOp.Subtract),
                ValueToken.Multiply => Binary(ExpressionOp.Multiply),
                ValueToken.Divide => Binary(ExpressionOp.Divide),
                ValueToken.Min => Binary(ExpressionOp.Minimum),
                ValueToken.Max => Binary(ExpressionOp.Maximum),
                ValueToken.Modulo => Binary(ExpressionOp.Modulo),
                ValueToken.Clamp => Ternary(ExpressionOp.Clamp),
                ValueToken.BitAnd => IntBinary(ExpressionOp.BitAnd),
                ValueToken.BitOr => IntBinary(ExpressionOp.BitOr),
                ValueToken.BitXor => IntBinary(ExpressionOp.BitXor),
                ValueToken.ShiftLeft => IntBinary(ExpressionOp.ShiftLeft),
                ValueToken.ShiftRight => IntBinary(ExpressionOp.ShiftRight),
                ValueToken.ShiftRightLogical => IntBinary(ExpressionOp.ShiftRightLogical),
                ValueToken.BitNot => IntUnary(ExpressionOp.BitNot),
                ValueToken.Equal => Comparison(ExpressionOp.Equal),
                ValueToken.NotEqual => Comparison(ExpressionOp.NotEqual),
                ValueToken.Less => Comparison(ExpressionOp.Less),
                ValueToken.LessOrEqual => Comparison(ExpressionOp.LessOrEqual),
                ValueToken.Greater => Comparison(ExpressionOp.Greater),
                ValueToken.GreaterOrEqual => Comparison(ExpressionOp.GreaterOrEqual),
                ValueToken.Select => Select(),
                ValueToken.PopCount => IntUnary(ExpressionOp.PopCount),
                ValueToken.LeadingZeroCount => IntUnary(ExpressionOp.LeadingZeroCount),
                ValueToken.TrailingZeroCount => IntUnary(ExpressionOp.TrailingZeroCount),
                ValueToken.LowestSetBit => IntUnary(ExpressionOp.LowestSetBit),
                ValueToken.ClearLowestSetBit => IntUnary(ExpressionOp.ClearLowestSetBit),
                ValueToken.ByteSwap => IntUnary(ExpressionOp.ByteSwap),
                ValueToken.BitReverse => IntUnary(ExpressionOp.BitReverse),
                ValueToken.RotateLeft => IntBinary(ExpressionOp.RotateLeft),
                ValueToken.RotateRight => IntBinary(ExpressionOp.RotateRight),
                ValueToken.Negate => Unary(ExpressionOp.Negate),
                ValueToken.Abs => Unary(ExpressionOp.Abs),
                ValueToken.Sign => SignOf(),
                ValueToken.ParallelBitExtract => IntBinary(ExpressionOp.ParallelBitExtract),
                ValueToken.ParallelBitDeposit => IntBinary(ExpressionOp.ParallelBitDeposit),
                ValueToken.BitField => IntArity(ExpressionOp.BitField, 3),
                ValueToken.BitInsert => IntArity(ExpressionOp.BitInsert, 4),
                ValueToken.Pair => IntBinary(ExpressionOp.Pair),
                ValueToken.PairX => IntUnary(ExpressionOp.PairX),
                ValueToken.PairY => IntUnary(ExpressionOp.PairY),
                ValueToken.PairSwap => IntUnary(ExpressionOp.PairSwap),
                ValueToken.PairMax => IntUnary(ExpressionOp.PairMax),
                ValueToken.PairMin => IntUnary(ExpressionOp.PairMin),
                ValueToken.PairSum => IntUnary(ExpressionOp.PairSum),
                ValueToken.PairDifference => IntUnary(ExpressionOp.PairDifference),
                ValueToken.PairTranslate => IntBinary(ExpressionOp.PairTranslate),
                ValueToken.PairScale => IntBinary(ExpressionOp.PairScale),
                ValueToken.Morton => IntBinary(ExpressionOp.Morton),
                ValueToken.MortonX => IntUnary(ExpressionOp.MortonX),
                ValueToken.MortonY => IntUnary(ExpressionOp.MortonY),
                ValueToken.Hilbert => IntArity(ExpressionOp.Hilbert, 3),
                ValueToken.HilbertX => IntBinary(ExpressionOp.HilbertX),
                ValueToken.HilbertY => IntBinary(ExpressionOp.HilbertY),
                ValueToken.HexIndex => IntBinary(ExpressionOp.HexIndex),
                ValueToken.HexQ => IntUnary(ExpressionOp.HexQ),
                ValueToken.HexR => IntUnary(ExpressionOp.HexR),
                ValueToken.HexRadius => IntUnary(ExpressionOp.HexRadius),
                ValueToken.HexEuclideanSquared => IntUnary(ExpressionOp.HexEuclideanSquared),
                ValueToken.HexDistance => IntBinary(ExpressionOp.HexDistance),
                ValueToken.HexNeighbor => IntBinary(ExpressionOp.HexNeighbor),
                ValueToken.HexRotate => IntBinary(ExpressionOp.HexRotate),
                ValueToken.HexMirror => IntUnary(ExpressionOp.HexMirror),
                ValueToken.HexSwap => IntUnary(ExpressionOp.HexSwap),
                ValueToken.HexAdd => IntBinary(ExpressionOp.HexAdd),
                ValueToken.HexSubtract => IntBinary(ExpressionOp.HexSubtract),
                ValueToken.HexMultiply => IntBinary(ExpressionOp.HexMultiply),
                ValueToken.HexScale => IntBinary(ExpressionOp.HexScale),
                ValueToken.HexTranslate => IntArity(ExpressionOp.HexTranslate, 3),
                ValueToken.Layer => IntArity(ExpressionOp.Layer, 4),
                ValueToken.LayerOffset => IntArity(ExpressionOp.LayerOffset, 4),
                ValueToken.LayerStart => IntArity(ExpressionOp.LayerStart, 4),
                ValueToken.LayerSize => IntArity(ExpressionOp.LayerSize, 4),
                ValueToken.SquareRoot => Unary(ExpressionOp.SquareRoot),
                ValueToken.Sine => FixedUnary(ExpressionOp.Sine),
                ValueToken.Cosine => FixedUnary(ExpressionOp.Cosine),
                ValueToken.SquareIndex => IntBinary(ExpressionOp.SquareIndex),
                ValueToken.SquareX => IntUnary(ExpressionOp.SquareX),
                ValueToken.SquareY => IntUnary(ExpressionOp.SquareY),
                ValueToken.SquareRadius => IntUnary(ExpressionOp.SquareRadius),
                ValueToken.SquareLength => IntUnary(ExpressionOp.SquareLength),
                ValueToken.SquareEuclideanSquared => IntUnary(ExpressionOp.SquareEuclideanSquared),
                ValueToken.SquareDistance => IntBinary(ExpressionOp.SquareDistance),
                ValueToken.SquareChebyshev => IntBinary(ExpressionOp.SquareChebyshev),
                ValueToken.SquareNeighbor => IntBinary(ExpressionOp.SquareNeighbor),
                ValueToken.SquareRotate => IntBinary(ExpressionOp.SquareRotate),
                ValueToken.SquareMirror => IntUnary(ExpressionOp.SquareMirror),
                ValueToken.SquareSwap => IntUnary(ExpressionOp.SquareSwap),
                ValueToken.SquareAdd => IntBinary(ExpressionOp.SquareAdd),
                ValueToken.SquareSubtract => IntBinary(ExpressionOp.SquareSubtract),
                ValueToken.SquareMultiply => IntBinary(ExpressionOp.SquareMultiply),
                ValueToken.SquareScale => IntBinary(ExpressionOp.SquareScale),
                ValueToken.SquareTranslate => IntArity(ExpressionOp.SquareTranslate, 3),
                ValueToken.GreatestCommonDivisor => IntBinary(ExpressionOp.GreatestCommonDivisor),
                ValueToken.LeastCommonMultiple => IntBinary(ExpressionOp.LeastCommonMultiple),
                ValueToken.FloorModulo => IntBinary(ExpressionOp.FloorModulo),
                ValueToken.CycleForward => IntArity(ExpressionOp.CycleForward, 3),
                ValueToken.CycleDistance => IntArity(ExpressionOp.CycleDistance, 3),
                ValueToken.SmallestMissing => IntUnary(ExpressionOp.SmallestMissing),
                ValueToken.IsPrime => IntUnary(ExpressionOp.IsPrime),
                ValueToken.Prime => IntUnary(ExpressionOp.Prime),
                ValueToken.Choose => IntBinary(ExpressionOp.Choose),
                ValueToken.Factorial => IntUnary(ExpressionOp.Factorial),
                ValueToken.SubsetRank => IntBinary(ExpressionOp.SubsetRank),
                ValueToken.SubsetAt => IntArity(ExpressionOp.SubsetAt, 3),
                ValueToken.SubsetMember => IntArity(ExpressionOp.SubsetMember, 4),
                ValueToken.ArrangementRank => IntBinary(ExpressionOp.ArrangementRank),
                ValueToken.ArrangementAt => IntBinary(ExpressionOp.ArrangementAt),
                ValueToken.ArrangementMember => IntArity(ExpressionOp.ArrangementMember, 3),
                ValueToken.BoardShift shift => ResolveBoardShift(shift),
                ValueToken.BoardFill fill => ResolveBoardFill(fill),
                ValueToken.BoardImage image => ResolveBoardImage(image),
                null => throw Malformed("contains a null token"),
                _ => throw Malformed($"contains unsupported token '{authored[index].GetType().Name}'"),
            };
        }

        if (depth != 1) {
            throw Malformed($"leaves {depth} values on the postfix stack instead of exactly one");
        }
        if (kinds[0] != kind) {
            throw Malformed($"leaves a kind={DescribeCellKind(kind: kinds[0])} value where kind={DescribeCellKind(kind: kind)} is required");
        }

        return tokens;

        CompiledExpressionToken ResolveState(ValueToken.State state) {
            var site = new OperandSite(RuleName: ruleName, Verb: verb, FieldLabel: "expression.state.name", KeyFieldLabel: "expression.state.key");
            var resolved = ResolveOperand(name: state.Name, key: state.Key, site: in site, context: context);

            if (resolved.ValueKind != kind) {
                throw new RuleException(
                    refusal: RuleRefusal.EffectSourceKindMismatch,
                    ruleName: ruleName,
                    detail: $"'{verb}' expression reads '{state.Name}' as kind={DescribeCellKind(kind: resolved.ValueKind)} into a kind={DescribeCellKind(kind: kind)} destination"
                );
            }

            return Push(new CompiledExpressionToken(Operation: ExpressionOp.Operand, Operand: resolved.Operand), kind);
        }
        CompiledExpressionToken Push(CompiledExpressionToken token, CellKind pushed) {
            kinds[depth++] = pushed;
            return token;
        }
        void Require(ExpressionOp operation, int arity) {
            if (depth < arity) { throw Malformed($"token '{operation}' underflows the postfix stack"); }
        }
        void RequireKind(ExpressionOp operation, int slot, CellKind required) {
            if (kinds[slot] != required) {
                throw Malformed($"token '{operation}' needs a kind={DescribeCellKind(kind: required)} operand but found kind={DescribeCellKind(kind: kinds[slot])}");
            }
        }
        CompiledExpressionToken Binary(ExpressionOp operation) {
            Require(operation, 2);
            RequireKind(operation, depth - 2, kind);
            RequireKind(operation, depth - 1, kind);
            depth--;
            return new CompiledExpressionToken(Operation: operation);
        }
        CompiledExpressionToken IntBinary(ExpressionOp operation) {
            if (kind != CellKind.Int) { throw Malformed($"token '{operation}' is admitted in kind=int expressions only"); }
            return Binary(operation);
        }
        CompiledExpressionToken IntUnary(ExpressionOp operation) {
            if (kind != CellKind.Int) { throw Malformed($"token '{operation}' is admitted in kind=int expressions only"); }
            Require(operation, 1);
            RequireKind(operation, depth - 1, CellKind.Int);
            return new CompiledExpressionToken(Operation: operation);
        }
        CompiledExpressionToken ResolveBoardShift(ValueToken.BoardShift shift) {
            if (kind != CellKind.Int) { throw Malformed("token 'BoardShift' is admitted in kind=int expressions only"); }
            if (context.FindTopology(name: (shift.Topology ?? string.Empty)) is not { } topology) {
                throw Malformed($"token 'BoardShift' names no discrete topology '{shift.Topology}'");
            }
            if (topology.CellCount > BoardMask.MaxCells) {
                throw Malformed($"token 'BoardShift' shifts a mask of at most {BoardMask.MaxCells} cells; '{shift.Topology}' has {topology.CellCount}");
            }
            var direction = topology.Direction(token: (shift.Direction ?? string.Empty));
            if (direction < 0) { throw Malformed($"token 'BoardShift' names no direction '{shift.Direction}' of '{shift.Topology}'"); }
            Require(ExpressionOp.BoardShift, 1);
            RequireKind(ExpressionOp.BoardShift, depth - 1, CellKind.Int);
            return new CompiledExpressionToken(Operation: ExpressionOp.BoardShift, Board: new BoardNeighbourQuery(topology: topology, direction: direction));
        }
        CompiledExpressionToken ResolveBoardFill(ValueToken.BoardFill fill) {
            if (kind != CellKind.Int) { throw Malformed("token 'BoardFill' is admitted in kind=int expressions only"); }
            if (context.FindTopology(name: (fill.Topology ?? string.Empty)) is not { } topology) {
                throw Malformed($"token 'BoardFill' names no discrete topology '{fill.Topology}'");
            }
            if (topology.CellCount > BoardMask.MaxCells) {
                throw Malformed($"token 'BoardFill' fills a mask of at most {BoardMask.MaxCells} cells; '{fill.Topology}' has {topology.CellCount}");
            }
            var direction = topology.Direction(token: (fill.Direction ?? string.Empty));
            if (direction < 0) { throw Malformed($"token 'BoardFill' names no direction '{fill.Direction}' of '{fill.Topology}'"); }
            Require(ExpressionOp.BoardFill, 1);
            RequireKind(ExpressionOp.BoardFill, depth - 1, CellKind.Int);
            return new CompiledExpressionToken(Operation: ExpressionOp.BoardFill, Board: new BoardNeighbourQuery(topology: topology, direction: direction));
        }
        CompiledExpressionToken ResolveBoardImage(ValueToken.BoardImage image) {
            if (kind != CellKind.Int) { throw Malformed("token 'BoardImage' is admitted in kind=int expressions only"); }
            if (context.FindTopology(name: (image.Topology ?? string.Empty)) is not { } topology) {
                throw Malformed($"token 'BoardImage' names no discrete topology '{image.Topology}'");
            }
            if (topology.CellCount > BoardMask.MaxCells) {
                throw Malformed($"token 'BoardImage' carries a mask of at most {BoardMask.MaxCells} cells; '{image.Topology}' has {topology.CellCount}");
            }
            var element = topology.Element(name: (image.Element ?? string.Empty));
            if (element < 0) { throw Malformed($"token 'BoardImage' names no symmetry element '{image.Element}' of '{image.Topology}'"); }
            Require(ExpressionOp.BoardImage, 1);
            RequireKind(ExpressionOp.BoardImage, depth - 1, CellKind.Int);
            return new CompiledExpressionToken(Operation: ExpressionOp.BoardImage, Board: new BoardNeighbourQuery(topology: topology, direction: element));
        }
        CompiledExpressionToken IntArity(ExpressionOp operation, int arity) {
            if (kind != CellKind.Int) { throw Malformed($"token '{operation}' is admitted in kind=int expressions only"); }
            Require(operation, arity);
            for (var slot = depth - arity; slot < depth; slot++) { RequireKind(operation, slot, CellKind.Int); }
            depth -= (arity - 1);
            return new CompiledExpressionToken(Operation: operation);
        }
        CompiledExpressionToken FixedUnary(ExpressionOp operation) {
            if (kind != CellKind.Fixed) { throw Malformed($"token '{operation}' is admitted in kind=fixed expressions only"); }
            Require(operation, 1);
            RequireKind(operation, depth - 1, CellKind.Fixed);
            return new CompiledExpressionToken(Operation: operation);
        }
        CompiledExpressionToken Unary(ExpressionOp operation) {
            Require(operation, 1);
            RequireKind(operation, depth - 1, kind);
            return new CompiledExpressionToken(Operation: operation);
        }
        CompiledExpressionToken SignOf() {
            Require(ExpressionOp.Sign, 1);
            if (kinds[depth - 1] is not (CellKind.Int or CellKind.Fixed)) {
                throw Malformed("token 'Sign' needs a numeric operand");
            }
            kinds[depth - 1] = CellKind.Int;
            return new CompiledExpressionToken(Operation: ExpressionOp.Sign);
        }
        CompiledExpressionToken Ternary(ExpressionOp operation) {
            Require(operation, 3);
            RequireKind(operation, depth - 3, kind);
            RequireKind(operation, depth - 2, kind);
            RequireKind(operation, depth - 1, kind);
            depth -= 2;
            return new CompiledExpressionToken(Operation: operation);
        }
        CompiledExpressionToken Comparison(ExpressionOp operation) {
            Require(operation, 2);
            if (kinds[depth - 2] != kinds[depth - 1]) {
                throw Malformed($"token '{operation}' compares kind={DescribeCellKind(kind: kinds[depth - 2])} against kind={DescribeCellKind(kind: kinds[depth - 1])}");
            }
            depth--;
            kinds[depth - 1] = CellKind.Int;
            return new CompiledExpressionToken(Operation: operation);
        }
        CompiledExpressionToken Select() {
            Require(ExpressionOp.Select, 3);
            RequireKind(ExpressionOp.Select, depth - 3, CellKind.Int);
            if (kinds[depth - 2] != kinds[depth - 1]) {
                throw Malformed($"token 'Select' branches disagree: kind={DescribeCellKind(kind: kinds[depth - 2])} against kind={DescribeCellKind(kind: kinds[depth - 1])}");
            }
            var result = kinds[depth - 1];
            depth -= 2;
            kinds[depth - 1] = result;
            return new CompiledExpressionToken(Operation: ExpressionOp.Select);
        }
        RuleException Malformed(string detail) => new(refusal: RuleRefusal.EffectSourceAmbiguous, ruleName: ruleName, detail: $"'{verb}' expression {detail}");
    }
}
