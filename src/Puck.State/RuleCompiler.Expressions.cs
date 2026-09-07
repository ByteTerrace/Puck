namespace Puck.State;

public static partial class RuleCompiler {
    /// <summary>Compiles a postfix value expression into its evaluated token program, proving it with a typed stack:
    /// every slot's kind is known at compile time, so an Int comparison result can feed a select inside a Fixed
    /// expression while never reaching an arithmetic operator of the wrong kind. The program leaves exactly one value
    /// of <paramref name="kind"/>. After validation, successful constant subtrees fold through the runtime
    /// evaluator; live reads and domain refusals remain in the evaluated program.</summary>
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
                ValueToken.BoardShift shift => ResolveBoardShift(shift),
                ValueToken.BoardFill fill => ResolveBoardFill(fill),
                ValueToken.BoardImage image => ResolveBoardImage(image),
                null => throw Malformed("contains a null token"),
                _ => ResolveOperator(authored[index]),
            };
        }

        if (depth != 1) {
            throw Malformed($"leaves {depth} values on the postfix stack instead of exactly one");
        }
        if (kinds[0] != kind) {
            throw Malformed($"leaves a kind={StateSpelling.Kind(kind: kinds[0])} value where kind={StateSpelling.Kind(kind: kind)} is required");
        }

        return FoldConstants(tokens, kind);

        CompiledExpressionToken ResolveState(ValueToken.State state) {
            var site = new OperandSite(RuleName: ruleName, Verb: verb, FieldLabel: "expression.state.name", KeyFieldLabel: "expression.state.key");
            var resolved = ResolveOperand(name: state.Name, key: state.Key, site: in site, context: context);

            if (resolved.ValueKind != kind) {
                throw new RuleException(
                    refusal: RuleRefusal.EffectSourceKindMismatch,
                    ruleName: ruleName,
                    detail: $"'{verb}' expression reads '{state.Name}' as kind={StateSpelling.Kind(kind: resolved.ValueKind)} into a kind={StateSpelling.Kind(kind: kind)} destination"
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
                throw Malformed($"token '{operation}' needs a kind={StateSpelling.Kind(kind: required)} operand but found kind={StateSpelling.Kind(kind: kinds[slot])}");
            }
        }
        CompiledExpressionToken ResolveOperator(ValueToken token) {
            var descriptor = ExpressionOperators.Find(token)
                ?? throw Malformed($"contains unsupported token '{token.GetType().Name}'");
            var operation = descriptor.Operation;
            if (descriptor.Signature == ExpressionSignature.Sign) { return SignOf(); }
            if (descriptor.Signature == ExpressionSignature.Comparison) { return Comparison(operation); }
            if (descriptor.Signature == ExpressionSignature.Select) { return Select(); }
            if (!descriptor.Admits(kind)) {
                throw Malformed($"token '{operation}' is admitted in kind={descriptor.Signature} expressions only");
            }
            Require(operation, descriptor.Arity);
            for (var slot = depth - descriptor.Arity; slot < depth; slot++) { RequireKind(operation, slot, kind); }
            depth -= descriptor.Arity - 1;
            return new CompiledExpressionToken(Operation: operation);
        }
        // The one door every board token (BoardShift/BoardFill/BoardImage) enters through: kind=Int only, a declared
        // discrete topology whose mask fits, then the token's own direction/element lookup (resolveIndex answers -1
        // for an unknown name) folded into the same neighbour query — three tokens, one refusal vocabulary.
        CompiledExpressionToken ResolveBoard(ExpressionOp operation, string? topologyName, string verbPhrase, string indexKind, string? indexName, Func<CompiledTopology, string, int> resolveIndex) {
            if (kind != CellKind.Int) { throw Malformed($"token '{operation}' is admitted in kind=Int expressions only"); }
            if (context.FindTopology(name: (topologyName ?? string.Empty)) is not { } topology) {
                throw Malformed($"token '{operation}' names no discrete topology '{topologyName}'");
            }
            if (topology.CellCount > BoardMask.MaxCells) {
                throw Malformed($"token '{operation}' {verbPhrase} a mask of at most {BoardMask.MaxCells} cells; '{topologyName}' has {topology.CellCount}");
            }
            var index = resolveIndex(topology, (indexName ?? string.Empty));
            if (index < 0) { throw Malformed($"token '{operation}' names no {indexKind} '{indexName}' of '{topologyName}'"); }
            Require(operation, 1);
            RequireKind(operation, depth - 1, CellKind.Int);
            return new CompiledExpressionToken(Operation: operation, Board: new BoardNeighbourQuery(topology: topology, direction: index));
        }
        CompiledExpressionToken ResolveBoardShift(ValueToken.BoardShift shift) =>
            ResolveBoard(ExpressionOp.BoardShift, shift.Topology, "shifts", "direction", shift.Direction, static (topology, token) => topology.Direction(token: token));
        CompiledExpressionToken ResolveBoardFill(ValueToken.BoardFill fill) =>
            ResolveBoard(ExpressionOp.BoardFill, fill.Topology, "fills", "direction", fill.Direction, static (topology, token) => topology.Direction(token: token));
        CompiledExpressionToken ResolveBoardImage(ValueToken.BoardImage image) =>
            ResolveBoard(ExpressionOp.BoardImage, image.Topology, "carries", "symmetry element", image.Element, static (topology, name) => topology.Element(name: name));
        CompiledExpressionToken SignOf() {
            Require(ExpressionOp.Sign, 1);
            if (kinds[depth - 1] is not (CellKind.Int or CellKind.Fixed)) {
                throw Malformed("token 'Sign' needs a numeric operand");
            }
            kinds[depth - 1] = CellKind.Int;
            return new CompiledExpressionToken(Operation: ExpressionOp.Sign);
        }
        CompiledExpressionToken Comparison(ExpressionOp operation) {
            Require(operation, 2);
            if (kinds[depth - 2] != kinds[depth - 1]) {
                throw Malformed($"token '{operation}' compares kind={StateSpelling.Kind(kind: kinds[depth - 2])} against kind={StateSpelling.Kind(kind: kinds[depth - 1])}");
            }
            depth--;
            kinds[depth - 1] = CellKind.Int;
            return new CompiledExpressionToken(Operation: operation);
        }
        CompiledExpressionToken Select() {
            Require(ExpressionOp.Select, 3);
            RequireKind(ExpressionOp.Select, depth - 3, CellKind.Int);
            if (kinds[depth - 2] != kinds[depth - 1]) {
                throw Malformed($"token 'Select' branches disagree: kind={StateSpelling.Kind(kind: kinds[depth - 2])} against kind={StateSpelling.Kind(kind: kinds[depth - 1])}");
            }
            var result = kinds[depth - 1];
            depth -= 2;
            kinds[depth - 1] = result;
            return new CompiledExpressionToken(Operation: ExpressionOp.Select);
        }
        RuleException Malformed(string detail) => new(refusal: RuleRefusal.EffectSourceAmbiguous, ruleName: ruleName, detail: $"'{verb}' expression {detail}");
    }
}
