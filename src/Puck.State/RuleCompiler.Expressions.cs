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
            throw new RuleException(
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName,
                detail: $"'{verb}' carries a null expression"
            );
        }
        if (
            (expression.Tokens is not { Count: > 0 } authored) ||
            (authored.Count > RuleCapacity.MaxExpressionTokens)
        ) {
            throw new RuleException(
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName,
                detail: $"'{verb}' expression must carry 1..{RuleCapacity.MaxExpressionTokens} postfix tokens"
            );
        }

        var tokens = new CompiledExpressionToken[authored.Count];
        var kinds = new CellKind[authored.Count];
        var depth = 0;

        for (var index = 0; (index < authored.Count); index++) {
            tokens[index] = authored[index] switch {
                ValueToken.Constant constant => Push(
                new CompiledExpressionToken(
                    Operation: ExpressionOp.Constant,
                    Constant: LiteralToRaw(
                        kind: kind,
                        literal: constant.Value,
                        ruleName: ruleName,
                        verb: verb
                    )
                ),
                kind
            ),
                ValueToken.State state => ResolveState(state: state),
                ValueToken.BoardShift shift => ResolveBoardShift(shift: shift),
                ValueToken.BoardFill fill => ResolveBoardFill(fill: fill),
                ValueToken.BoardImage image => ResolveBoardImage(image: image),
                ValueToken.VectorCall vectorCall => ResolveVectorCall(call: vectorCall),
                null => throw Malformed(detail: "contains a null token"),
                _ => ResolveOperator(token: authored[index]),
            };
        }

        if (depth != 1) {
            throw Malformed(detail: $"leaves {depth} values on the postfix stack instead of exactly one");
        }
        if (kinds[0] != kind) {
            throw Malformed(detail: $"leaves a kind={StateSpelling.Kind(kind: kinds[0])} value where kind={StateSpelling.Kind(kind: kind)} is required");
        }

        return FoldConstants(
            kind: kind,
            tokens: tokens
        );

        CompiledExpressionToken ResolveState(ValueToken.State state) {
            var site = new OperandSite(
                RuleName: ruleName,
                Verb: verb,
                FieldLabel: "expression.state.name",
                KeyFieldLabel: "expression.state.key"
            );
            var resolved = ResolveOperand(
                name: state.Name,
                key: state.Key,
                site: in site,
                context: context
            );

            if (resolved.ValueKind != kind) {
                throw new RuleException(
                    refusal: RuleRefusal.EffectSourceKindMismatch,
                    ruleName: ruleName,
                    detail: $"'{verb}' expression reads '{state.Name}' as kind={StateSpelling.Kind(kind: resolved.ValueKind)} into a kind={StateSpelling.Kind(kind: kind)} destination"
                );
            }

            return Push(
                new CompiledExpressionToken(
                    Operation: ExpressionOp.Operand,
                    Operand: resolved.Operand
                ),
                kind
            );
        }
        CompiledExpressionToken Push(CompiledExpressionToken token, CellKind pushed) {
            kinds[depth++] = pushed;
            return token;
        }
        void Require(ExpressionOp operation, int arity) {
            if (depth < arity) { throw Malformed(detail: $"token '{operation}' underflows the postfix stack"); }
        }
        void RequireKind(ExpressionOp operation, int slot, CellKind required) {
            if (kinds[slot] != required) {
                throw Malformed(detail: $"token '{operation}' needs a kind={StateSpelling.Kind(kind: required)} operand but found kind={StateSpelling.Kind(kind: kinds[slot])}");
            }
        }
        CompiledExpressionToken ResolveOperator(ValueToken token) {
            var descriptor = (ExpressionOperators.Find(token: token)
                ?? throw Malformed(detail: $"contains unsupported token '{token.GetType().Name}'"));
            var operation = descriptor.Operation;

            if (descriptor.Signature == ExpressionSignature.Sign) { return SignOf(); }
            if (descriptor.Signature == ExpressionSignature.Comparison) { return Comparison(operation: operation); }
            if (descriptor.Signature == ExpressionSignature.Select) { return Select(); }
            if (!descriptor.Admits(kind: kind)) {
                throw Malformed(detail: $"token '{operation}' is admitted in kind={descriptor.Signature} expressions only");
            }
            Require(
                operation,
                descriptor.Arity
            );
            for (var slot = (depth - descriptor.Arity); (slot < depth); slot++) { RequireKind(
                operation: operation,
                required: kind,
                slot: slot
            ); }
            depth -= (descriptor.Arity - 1);
            return new CompiledExpressionToken(Operation: operation);
        }
        // The one door every board token (BoardShift/BoardFill/BoardImage) enters through: kind=Int only, a declared
        // discrete topology whose mask fits, then the token's own direction/element lookup (resolveIndex answers -1
        // for an unknown name) folded into the same neighbour query — three tokens, one refusal vocabulary.
        CompiledExpressionToken ResolveBoard(ExpressionOp operation, string? topologyName, string verbPhrase, string indexKind, string? indexName, Func<CompiledTopology, string, int> resolveIndex) {
            if (kind != CellKind.Int) { throw Malformed(detail: $"token '{operation}' is admitted in kind=Int expressions only"); }
            if (context.FindTopology(name: (topologyName ?? string.Empty)) is not { } topology) {
                throw Malformed(detail: $"token '{operation}' names no discrete topology '{topologyName}'");
            }
            if (topology.CellCount > BoardMask.MaxCells) {
                throw Malformed(detail: $"token '{operation}' {verbPhrase} a mask of at most {BoardMask.MaxCells} cells; '{topologyName}' has {topology.CellCount}");
            }
            var index = resolveIndex(
                topology,
                (indexName ?? string.Empty)
            );

            if (index < 0) { throw Malformed(detail: $"token '{operation}' names no {indexKind} '{indexName}' of '{topologyName}'"); }
            Require(
                arity: 1,
                operation: operation
            );
            RequireKind(
                operation: operation,
                required: CellKind.Int,
                slot: (depth - 1)
            );
            return new CompiledExpressionToken(
                Operation: operation,
                Board: new BoardNeighbourQuery(
                    direction: index,
                    topology: topology
                )
            );
        }
        CompiledExpressionToken ResolveBoardShift(ValueToken.BoardShift shift) =>
            ResolveBoard(
                ExpressionOp.BoardShift,
                shift.Topology,
                "shifts",
                "direction",
                shift.Direction,
                static (topology, token) => topology.Direction(token: token)
            );
        CompiledExpressionToken ResolveBoardFill(ValueToken.BoardFill fill) =>
            ResolveBoard(
                ExpressionOp.BoardFill,
                fill.Topology,
                "fills",
                "direction",
                fill.Direction,
                static (topology, token) => topology.Direction(token: token)
            );
        CompiledExpressionToken ResolveBoardImage(ValueToken.BoardImage image) =>
            ResolveBoard(
                ExpressionOp.BoardImage,
                image.Topology,
                "carries",
                "symmetry element",
                image.Element,
                static (topology, name) => topology.Element(name: name)
            );
        CompiledExpressionToken SignOf() {
            Require(
                arity: 1,
                operation: ExpressionOp.Sign
            );
            if (kinds[(depth - 1)] is not (CellKind.Int or CellKind.Fixed)) {
                throw Malformed(detail: "token 'Sign' needs a numeric operand");
            }
            kinds[(depth - 1)] = CellKind.Int;
            return new CompiledExpressionToken(Operation: ExpressionOp.Sign);
        }
        CompiledExpressionToken Comparison(ExpressionOp operation) {
            Require(
                arity: 2,
                operation: operation
            );
            if (kinds[(depth - 2)] != kinds[(depth - 1)]) {
                throw Malformed(detail: $"token '{operation}' compares kind={StateSpelling.Kind(kind: kinds[(depth - 2)])} against kind={StateSpelling.Kind(kind: kinds[(depth - 1)])}");
            }
            depth--;
            kinds[(depth - 1)] = CellKind.Int;
            return new CompiledExpressionToken(Operation: operation);
        }
        CompiledExpressionToken Select() {
            Require(
                arity: 3,
                operation: ExpressionOp.Select
            );
            RequireKind(
                operation: ExpressionOp.Select,
                required: CellKind.Int,
                slot: (depth - 3)
            );
            if (kinds[(depth - 2)] != kinds[(depth - 1)]) {
                throw Malformed(detail: $"token 'Select' branches disagree: kind={StateSpelling.Kind(kind: kinds[(depth - 2)])} against kind={StateSpelling.Kind(kind: kinds[(depth - 1)])}");
            }
            var result = kinds[(depth - 1)];

            depth -= 2;
            kinds[(depth - 1)] = result;
            return new CompiledExpressionToken(Operation: ExpressionOp.Select);
        }
        CompiledExpressionToken ResolveVectorCall(ValueToken.VectorCall call) {
            if (call.Operation is not (ExpressionOp.Dot or ExpressionOp.Similarity or ExpressionOp.Identical)) {
                throw Malformed(detail: $"token '{call.Operation}' is not a supported vector function");
            }

            var opKind = (call.Operation == ExpressionOp.Similarity) ? CellKind.Fixed : CellKind.Int;

            CompiledVectorOperand left;
            CompiledVectorOperand right;

            if (call.Left is VectorOperandToken.Cell) {
                left = ResolveVectorOperand(
                    token: call.Left,
                    context: context,
                    ruleName: ruleName,
                    where: $"{verb} vector {call.Operation}"
                );
                right = ResolveVectorOperand(
                    token: call.Right,
                    context: context,
                    ruleName: ruleName,
                    where: $"{verb} vector {call.Operation}",
                    expectedSpace: left.Space
                );
            } else if (call.Right is VectorOperandToken.Cell) {
                right = ResolveVectorOperand(
                    token: call.Right,
                    context: context,
                    ruleName: ruleName,
                    where: $"{verb} vector {call.Operation}"
                );
                left = ResolveVectorOperand(
                    token: call.Left,
                    context: context,
                    ruleName: ruleName,
                    where: $"{verb} vector {call.Operation}",
                    expectedSpace: right.Space
                );
            } else {
                var defaultSpace = context.FindSpace(name: null)
                    ?? throw new RuleException(
                        refusal: RuleRefusal.VectorSpaceMismatch,
                        ruleName: ruleName,
                        detail: $"'{verb}' vector literal has no space; declare a default space or address a typed vector row"
                    );
                left = ResolveVectorOperand(
                    token: call.Left,
                    context: context,
                    ruleName: ruleName,
                    where: $"{verb} vector {call.Operation}",
                    expectedSpace: defaultSpace
                );
                right = ResolveVectorOperand(
                    token: call.Right,
                    context: context,
                    ruleName: ruleName,
                    where: $"{verb} vector {call.Operation}",
                    expectedSpace: defaultSpace
                );
            }

            if (!string.Equals(left.Space.Name.Value, right.Space.Name.Value, StringComparison.Ordinal) || !left.Space.HasSameIdentity(other: right.Space)) {
                throw new RuleException(
                    refusal: RuleRefusal.VectorSpaceMismatch,
                    ruleName: ruleName,
                    detail: $"'{verb}' vector call spaces mismatch: left is '{left.Space.Name}', right is '{right.Space.Name}'"
                );
            }

            var operandFact = new VectorCallOperand(
                operation: call.Operation,
                left: left,
                right: right,
                valueKind: opKind
            );

            return Push(
                new CompiledExpressionToken(
                    Operation: ExpressionOp.Operand,
                    Operand: operandFact
                ),
                opKind
            );
        }
        RuleException Malformed(string detail) => new(
            refusal: RuleRefusal.EffectSourceAmbiguous,
            ruleName: ruleName,
            detail: $"'{verb}' expression {detail}"
        );
    }
}
