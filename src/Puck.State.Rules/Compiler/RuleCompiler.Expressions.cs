namespace Puck.State.Rules;

public static partial class RuleCompiler {
    /// <summary>Compiles a postfix expression program into its evaluated token program, proving it with a typed
    /// stack: every slot's kind is known at compile time, so an Int comparison result can feed a select inside a
    /// Fixed expression while never reaching an arithmetic operator of the wrong kind. The program leaves exactly
    /// one value of <paramref name="kind"/>.</summary>
    /// <param name="expression">The authored program.</param>
    /// <param name="kind">The kind the expression must produce — the destination cell's, or the binding's.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    /// <param name="context">The compile context.</param>
    /// <returns>The evaluated token program.</returns>
    /// <exception cref="RuleException">The expression is missing, over-long, ill-typed, or names something the
    /// section does not declare.</exception>
    public static CompiledExpressionToken[] CompileExpression(ExpressionProgram? expression, CellKind kind, string ruleName, string verb, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (expression is null) {
            throw new RuleException(
                detail: $"'{verb}' carries a null expression",
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName
            );
        }
        if (expression.Subprograms.Count > RuleCapacity.MaxSubprograms) {
            throw new RuleException(
                detail: $"'{verb}' expression carries {expression.Subprograms.Count} subprograms; at most {RuleCapacity.MaxSubprograms} are admitted",
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName
            );
        }

        return CompileInstructions(
            argumentKinds: [],
            calling: [],
            context: context,
            expression: expression,
            instructions: expression.Instructions,
            kind: kind,
            memberKind: null,
            result: out _,
            resultKind: kind,
            ruleName: ruleName,
            shared: new Dictionary<string, (CompiledExpressionToken[] Body, CellKind Result)>(comparer: StringComparer.Ordinal),
            verb: verb
        );
    }

    // Validation precedes folding: every authored operand, kind, and token still has to be legal. Only a
    // successful reader-free subtree becomes a raw constant. A domain refusal stays in the program, including an
    // unselected ternary branch: expressions are eager, so folding must not hide that refusal.
    private static CompiledExpressionToken[] FoldConstants(CompiledExpressionToken[] tokens, CellKind kind) {
        var constants = new bool[tokens.Length];
        var starts = new int[tokens.Length];
        var count = 0;
        var depth = 0;
        var output = new CompiledExpressionToken[tokens.Length];

        foreach (var token in tokens) {
            var arity = ((token.Call is { } call)
                ? call.Arity
                : ExpressionOperators.Arity(operation: token.Operation)
            );
            var start = ((arity == 0)
                ? count
                : starts[(depth - arity)]
            );
            // A read of live state, of a fold member, or of a call's own operand has no value at compile time, and
            // neither does a fold over a family.
            var constant = (token.Operation is not (ExpressionOp.Operand or ExpressionOp.Member or ExpressionOp.Argument or ExpressionOp.All or ExpressionOp.Any or ExpressionOp.Count or ExpressionOp.Sum));

            for (var argument = (depth - arity); (argument < depth); argument++) {
                constant &= constants[argument];
            }

            depth -= arity;
            output[count++] = token;
            // Folding evaluates the subtree here and now, before anything has priced it. A subtree too long to fold
            // stays in the program, where the work sheet prices it like any other.
            if (
                constant &&
                (arity != 0) &&
                (RuleWorkBudget.Steps(tokens: output.AsSpan(
                    length: (count - start),
                    start: start
                )) > RuleWorkBudget.MaxFoldSteps)
            ) {
                constant = false;
            }
            if (
                constant &&
                (arity != 0)
            ) {
                constant = RuleExpressions.TryEvaluate(
                    fault: out _,
                    kind: kind,
                    program: output.AsSpan(
                        length: (count - start),
                        start: start
                    ),
                    reader: null,
                    value: out var value
                );
                if (constant) {
                    count = start;
                    output[count++] = new CompiledExpressionToken(
                        Constant: value,
                        Operation: ExpressionOp.Constant
                    );
                }
            }

            constants[depth] = constant;
            starts[depth++] = start;
        }

        return ((count == tokens.Length)
            ? tokens
            : output[..count]
        );
    }
    private static ExpressionOp Flip(ExpressionOp operation) => (operation switch {
        ExpressionOp.Less => ExpressionOp.Greater,
        ExpressionOp.LessOrEqual => ExpressionOp.GreaterOrEqual,
        ExpressionOp.Greater => ExpressionOp.Less,
        ExpressionOp.GreaterOrEqual => ExpressionOp.LessOrEqual,
        _ => operation,
    });
    private static ActionStateComparison ComparisonOf(ExpressionOp operation) => (operation switch {
        ExpressionOp.Equal => ActionStateComparison.Equal,
        ExpressionOp.NotEqual => ActionStateComparison.NotEqual,
        ExpressionOp.Less => ActionStateComparison.Less,
        ExpressionOp.LessOrEqual => ActionStateComparison.LessOrEqual,
        ExpressionOp.Greater => ActionStateComparison.Greater,
        _ => ActionStateComparison.GreaterOrEqual,
    });
    private static ExpressionOp OperationOf(ActionStateComparison comparison) => (comparison switch {
        ActionStateComparison.Equal => ExpressionOp.Equal,
        ActionStateComparison.NotEqual => ExpressionOp.NotEqual,
        ActionStateComparison.Less => ExpressionOp.Less,
        ActionStateComparison.LessOrEqual => ExpressionOp.LessOrEqual,
        ActionStateComparison.Greater => ExpressionOp.Greater,
        _ => ExpressionOp.GreaterOrEqual,
    });
    // The one walk every program, subprogram and fold body enters through. `memberKind` is the kind a bound family
    // member reads as inside a fold body; `argumentKinds` are the kinds a subprogram's own operands carry;
    // `calling` is the chain of subprograms already open, which is what makes the call graph a proven DAG; and
    // `shared` holds each subprogram body compiled once per signature.
    private static CompiledExpressionToken[] CompileInstructions(ExpressionProgram expression, IReadOnlyList<Instruction> instructions, CellKind kind, CellKind? resultKind, CellKind? memberKind, IReadOnlyList<CellKind> argumentKinds, IReadOnlyList<int> calling, string ruleName, string verb, RuleCompileContext context, Dictionary<string, (CompiledExpressionToken[] Body, CellKind Result)> shared, out CellKind result) {
        if (
            (instructions is not { Count: > 0 } authored) ||
            (authored.Count > RuleCapacity.MaxExpressionTokens)
        ) {
            throw new RuleException(
                detail: $"'{verb}' expression must carry 1..{RuleCapacity.MaxExpressionTokens} postfix instructions",
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName
            );
        }

        var depth = 0;
        var kinds = new CellKind[authored.Count];
        // Per stack slot: the token index of the one constant instruction that produced it, or -1, and the exact
        // decimal that constant was authored as. A comparison reads both to lower a fractional literal exactly.
        var literalAt = new int[authored.Count];
        var literals = new decimal[authored.Count];
        var tokens = new CompiledExpressionToken[authored.Count];

        for (var index = 0; (index < authored.Count); index++) {
            var instruction = (authored[index] ?? throw Malformed(detail: "contains a null instruction"));

            tokens[index] = (instruction switch {
                { Payload: InstructionPayload.Constant constant } => PushConstant(
                    constant: constant,
                    index: index
                ),
                { Payload: InstructionPayload.State state } => ResolveState(state: state),
                { Payload: InstructionPayload.Board board } => ResolveBoard(
                    board: board,
                    operation: instruction.Operation
                ),
                { Payload: InstructionPayload.Vector vector } => ResolveVectorCall(
                    call: vector,
                    operation: instruction.Operation
                ),
                { Payload: InstructionPayload.Fold fold } => ResolveFold(
                    fold: fold,
                    operation: instruction.Operation
                ),
                { Payload: InstructionPayload.Call call } => ResolveCall(call: call),
                { Payload: InstructionPayload.Argument argument } => ResolveArgument(argument: argument),
                { Operation: ExpressionOp.Member } => ResolveMember(),
                _ => ResolveOperator(instruction: instruction),
            });

            // Only a constant instruction leaves a literal on the stack; every other instruction's result slot is
            // not one.
            if (instruction.Payload is not InstructionPayload.Constant) {
                literalAt[(depth - 1)] = -1;
            }
        }

        if (depth != 1) {
            throw Malformed(detail: $"leaves {depth} values on the postfix stack instead of exactly one");
        }
        if ((resultKind is { } required)
            ? (kinds[0] != required)
            : ((kinds[0] != kind) && (kinds[0] != CellKind.Int))
        ) {
            throw Malformed(detail: $"leaves a kind={StateSpelling.Kind(kind: kinds[0])} value where kind={StateSpelling.Kind(kind: (resultKind ?? kind))} is required");
        }

        result = kinds[0];

        return FoldConstants(
            kind: kind,
            tokens: tokens
        );

        CompiledExpressionToken Absence() {
            Require(
                arity: 1,
                operation: ExpressionOp.IsAbsent
            );
            kinds[(depth - 1)] = CellKind.Int;

            return new CompiledExpressionToken(Operation: ExpressionOp.IsAbsent);
        }
        CompiledExpressionToken Coalesce() {
            Require(
                arity: 2,
                operation: ExpressionOp.Coalesce
            );
            if (kinds[(depth - 2)] != kinds[(depth - 1)]) {
                throw Malformed(detail: $"instruction 'Coalesce' arms disagree: kind={StateSpelling.Kind(kind: kinds[(depth - 2)])} against kind={StateSpelling.Kind(kind: kinds[(depth - 1)])}");
            }

            depth--;

            return new CompiledExpressionToken(Operation: ExpressionOp.Coalesce);
        }
        CompiledExpressionToken Comparison(ExpressionOp operation) {
            Require(
                arity: 2,
                operation: operation
            );
            if (kinds[(depth - 2)] != kinds[(depth - 1)]) {
                throw Malformed(detail: $"instruction '{operation}' compares kind={StateSpelling.Kind(kind: kinds[(depth - 2)])} against kind={StateSpelling.Kind(kind: kinds[(depth - 1)])}");
            }

            var compared = kinds[(depth - 1)];

            if (compared == CellKind.Int) {
                operation = LowerFractionalComparison(operation: operation);
            }

            depth--;
            kinds[(depth - 1)] = CellKind.Int;

            return new CompiledExpressionToken(Operation: operation);
        }
        IReadOnlyList<int> Entering(int subprogram) {
            if (calling.Contains(value: subprogram)) {
                throw Malformed(detail: $"calls '{expression.Subprograms[subprogram].Name}', which is already open in this call chain; the call graph carries no cycle");
            }

            return [.. calling, subprogram];
        }
        // A fractional literal compared against an Int operand lowers through the one conversion table
        // LowerConstantComparison states, so the expression spelling and compareState agree by construction.
        ExpressionOp LowerFractionalComparison(ExpressionOp operation) {
            var left = (depth - 2);
            var right = (depth - 1);
            var flipped = false;
            var slot = right;

            if (literalAt[right] < 0) {
                if (
                    (literalAt[left] < 0) ||
                    (decimal.Truncate(d: literals[left]) == literals[left])
                ) {
                    return operation;
                }

                flipped = true;
                operation = Flip(operation: operation);
                slot = left;
            } else if (decimal.Truncate(d: literals[right]) == literals[right]) {
                return operation;
            }

            var (raw, lowered) = LowerConstantComparison(
                comparison: ComparisonOf(operation: operation),
                kind: CellKind.Int,
                literal: literals[slot],
                ruleName: ruleName
            );

            tokens[literalAt[slot]] = new CompiledExpressionToken(
                Constant: raw,
                Operation: ExpressionOp.Constant
            );

            return (flipped
                ? Flip(operation: OperationOf(comparison: lowered))
                : OperationOf(comparison: lowered)
            );
        }
        RuleException Malformed(string detail) => new(
            detail: $"'{verb}' expression {detail}",
            refusal: RuleRefusal.EffectSourceAmbiguous,
            ruleName: ruleName
        );
        CompiledExpressionToken Push(CompiledExpressionToken token, CellKind pushed) {
            kinds[depth++] = pushed;

            return token;
        }
        CompiledExpressionToken PushConstant(InstructionPayload.Constant constant, int index) {
            literalAt[depth] = index;
            literals[depth] = constant.Value;

            return Push(
                new CompiledExpressionToken(
                    Constant: LiteralToRaw(
                        kind: kind,
                        literal: constant.Value,
                        ruleName: ruleName,
                        verb: verb
                    ),
                    Operation: ExpressionOp.Constant
                ),
                kind
            );
        }
        void Require(ExpressionOp operation, int arity) {
            if (depth < arity) {
                throw Malformed(detail: $"instruction '{operation}' underflows the postfix stack");
            }
        }
        void RequireKind(ExpressionOp operation, int slot, CellKind required) {
            if (kinds[slot] != required) {
                throw Malformed(detail: $"instruction '{operation}' needs a kind={StateSpelling.Kind(kind: required)} operand but found kind={StateSpelling.Kind(kind: kinds[slot])}");
            }
        }
        CompiledExpressionToken ResolveArgument(InstructionPayload.Argument argument) {
            if (((uint)argument.Index) >= ((uint)argumentKinds.Count)) {
                throw Malformed(detail: $"reads argument {argument.Index} of a subprogram taking {argumentKinds.Count}");
            }

            return Push(
                new CompiledExpressionToken(
                    Constant: argument.Index,
                    Operation: ExpressionOp.Argument
                ),
                argumentKinds[argument.Index]
            );
        }
        // The one door every board instruction enters through: kind=Int only, a declared discrete topology whose
        // mask fits, then the instruction's own direction/element lookup folded into the same neighbour query.
        CompiledExpressionToken ResolveBoard(ExpressionOp operation, InstructionPayload.Board board) {
            var (verbPhrase, indexKind) = (operation switch {
                ExpressionOp.BoardShift => ("shifts", "direction"),
                ExpressionOp.BoardFill => ("fills", "direction"),
                ExpressionOp.BoardImage => ("carries", "symmetry element"),
                _ => throw Malformed(detail: $"instruction '{operation}' is not a board query"),
            });

            if (kind != CellKind.Int) {
                throw Malformed(detail: $"instruction '{operation}' is admitted in kind=Int expressions only");
            }
            if (context.FindTopology(name: board.Topology) is not { } topology) {
                throw Malformed(detail: $"instruction '{operation}' names no discrete topology '{board.Topology}'");
            }
            if (topology.CellCount > BoardMask.MaxCells) {
                throw Malformed(detail: $"instruction '{operation}' {verbPhrase} a mask of at most {BoardMask.MaxCells} cells; '{board.Topology}' has {topology.CellCount}");
            }

            var resolved = ((operation == ExpressionOp.BoardImage)
                ? topology.Element(name: board.Index)
                : topology.Direction(token: board.Index)
            );

            if (resolved < 0) {
                throw Malformed(detail: $"instruction '{operation}' names no {indexKind} '{board.Index}' of '{board.Topology}'");
            }

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
                Board: new BoardNeighbourQuery(
                    direction: resolved,
                    topology: topology
                ),
                Operation: operation
            );
        }
        CompiledExpressionToken ResolveCall(InstructionPayload.Call call) {
            if (((uint)call.Subprogram) >= ((uint)expression.Subprograms.Count)) {
                throw Malformed(detail: $"calls subprogram {call.Subprogram}, which the program does not carry");
            }

            var declared = expression.Subprograms[call.Subprogram];

            if (declared.Arity > RuleExpressions.MaxArguments) {
                throw Malformed(detail: $"calls a function of {declared.Arity} arguments; a function takes at most {RuleExpressions.MaxArguments}");
            }

            Require(
                arity: declared.Arity,
                operation: ExpressionOp.Call
            );

            var arguments = new CellKind[declared.Arity];

            for (var slot = 0; (slot < declared.Arity); slot++) {
                arguments[slot] = kinds[((depth - declared.Arity) + slot)];
            }

            var (body, returned) = SharedBody(
                argumentKinds: arguments,
                kind: kind,
                memberKind: memberKind,
                resultKind: null,
                subprogram: call.Subprogram
            );

            depth -= declared.Arity;

            return Push(
                new CompiledExpressionToken(
                    Call: new CompiledSubprogram(
                        Arity: declared.Arity,
                        Body: body,
                        Name: declared.Name,
                        Steps: RuleWorkBudget.Steps(tokens: body)
                    ),
                    Operation: ExpressionOp.Call
                ),
                returned
            );
        }
        CompiledExpressionToken ResolveFold(ExpressionOp operation, InstructionPayload.Fold fold) {
            if (memberKind is not null) {
                throw Malformed(detail: $"nests the fold '{fold.Family}' inside another; a fold nests at most once");
            }
            if (((uint)fold.Subprogram) >= ((uint)expression.Subprograms.Count)) {
                throw Malformed(detail: $"folds '{fold.Family}' through subprogram {fold.Subprogram}, which the program does not carry");
            }
            if (context.FindRow(name: fold.Family) is not { } family) {
                throw Malformed(detail: $"folds '{fold.Family}', which the section does not declare");
            }

            var member = family.Kind;

            if (member is not (CellKind.Int or CellKind.Fixed)) {
                throw Malformed(detail: $"folds '{fold.Family}', whose kind={StateSpelling.Kind(kind: member)} cells are not numeric");
            }
            if (
                (operation == ExpressionOp.Sum) &&
                (member != kind)
            ) {
                throw Malformed(detail: $"sums kind={StateSpelling.Kind(kind: member)} '{fold.Family}' into a kind={StateSpelling.Kind(kind: kind)} destination");
            }

            // The body computes in its member's kind; a predicate fold's body leaves the Int its comparison yields,
            // and the fold pushes that Int whatever kind the enclosing program computes in.
            var (body, _) = SharedBody(
                argumentKinds: argumentKinds,
                kind: member,
                memberKind: member,
                resultKind: ((operation == ExpressionOp.Sum)
                    ? member
                    : CellKind.Int),
                subprogram: fold.Subprogram
            );

            return Push(
                new CompiledExpressionToken(
                    Fold: new CompiledFold(
                        Body: body,
                        Describe: fold.Family,
                        MemberKind: member,
                        Members: (family.Cells?.Count ?? 0),
                        Operation: operation,
                        RowOrdinal: ResolveRowOrdinal(
                            context: context,
                            name: fold.Family
                        )
                    ),
                    Operation: operation
                ),
                ((operation == ExpressionOp.Sum)
                    ? member
                    : CellKind.Int)
            );
        }
        CompiledExpressionToken ResolveMember() {
            if (memberKind is not { } bound) {
                throw Malformed(detail: "reads a fold member outside a fold body");
            }
            if (bound != kind) {
                throw Malformed(detail: $"reads a kind={StateSpelling.Kind(kind: bound)} fold member into a kind={StateSpelling.Kind(kind: kind)} body");
            }

            return Push(
                new CompiledExpressionToken(Operation: ExpressionOp.Member),
                kind
            );
        }
        CompiledExpressionToken ResolveOperator(Instruction instruction) {
            var descriptor = (ExpressionOperators.Find(instruction: instruction) ?? throw Malformed(detail: $"contains unsupported instruction '{instruction.Operation}'"));
            var operation = descriptor.Operation;

            if (descriptor.Payload != PayloadShape.None) {
                throw Malformed(detail: $"instruction '{operation}' carries no {descriptor.Payload} payload");
            }
            if (descriptor.Signature == ExpressionSignature.Sign) {
                return SignOf();
            }
            if (descriptor.Signature == ExpressionSignature.Comparison) {
                return Comparison(operation: operation);
            }
            if (descriptor.Signature == ExpressionSignature.Select) {
                return Select();
            }
            if (descriptor.Signature == ExpressionSignature.Absence) {
                return Absence();
            }
            if (descriptor.Signature == ExpressionSignature.Coalesce) {
                return Coalesce();
            }
            if (!descriptor.Admits(kind: kind)) {
                throw Malformed(detail: $"instruction '{operation}' is admitted in kind={descriptor.Signature} expressions only");
            }

            Require(
                arity: descriptor.Arity,
                operation: operation
            );
            for (var slot = (depth - descriptor.Arity); (slot < depth); slot++) {
                RequireKind(
                    operation: operation,
                    required: kind,
                    slot: slot
                );
            }

            depth -= (descriptor.Arity - 1);

            return new CompiledExpressionToken(Operation: operation);
        }
        CompiledExpressionToken ResolveState(InstructionPayload.State state) {
            var site = new OperandSite(
                FieldLabel: "expression.state.name",
                KeyFieldLabel: "expression.state.key",
                RuleName: ruleName,
                Verb: verb
            );
            var resolved = ResolveOperand(
                context: context,
                key: state.Key,
                name: state.Name,
                site: in site
            );

            // A Bool cell reads as the 0 or 1 it stores, so it is an Int operand. It stays refused in a Fixed
            // expression, where 0 or 1 raw is 0 or 2^-16 rather than zero or one.
            if (
                (resolved.ValueKind != kind) &&
                !((kind == CellKind.Int) && (resolved.ValueKind == CellKind.Bool))
            ) {
                throw new RuleException(
                    detail: $"'{verb}' expression reads '{state.Name}' as kind={StateSpelling.Kind(kind: resolved.ValueKind)} into a kind={StateSpelling.Kind(kind: kind)} destination",
                    refusal: RuleRefusal.EffectSourceKindMismatch,
                    ruleName: ruleName
                );
            }

            return Push(
                new CompiledExpressionToken(
                    Operand: resolved.Operand,
                    Operation: ExpressionOp.Operand
                ),
                kind
            );
        }
        CompiledExpressionToken ResolveVectorCall(ExpressionOp operation, InstructionPayload.Vector call) {
            if (operation is not (ExpressionOp.Dot or ExpressionOp.Similarity or ExpressionOp.Identical)) {
                throw Malformed(detail: $"instruction '{operation}' is not a supported vector function");
            }

            var operandKind = ((operation == ExpressionOp.Similarity)
                ? CellKind.Fixed
                : CellKind.Int
            );
            var fact = ResolveVectorCallOperand(
                call: call,
                context: context,
                operandKind: operandKind,
                operation: operation,
                ruleName: ruleName,
                verb: verb
            );

            context.Needs.AddFact(fact: fact);

            return Push(
                new CompiledExpressionToken(
                    Operand: fact,
                    Operation: ExpressionOp.Operand
                ),
                operandKind
            );
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
                throw Malformed(detail: $"instruction 'Select' branches disagree: kind={StateSpelling.Kind(kind: kinds[(depth - 2)])} against kind={StateSpelling.Kind(kind: kinds[(depth - 1)])}");
            }

            var selected = kinds[(depth - 1)];

            depth -= 2;
            kinds[(depth - 1)] = selected;

            return new CompiledExpressionToken(Operation: ExpressionOp.Select);
        }
        // A subprogram body depends only on the subprogram and the kinds it is entered with, so one compiled body
        // per signature serves every call site and every fold that names it.
        (CompiledExpressionToken[] Body, CellKind Result) SharedBody(int subprogram, CellKind kind, CellKind? resultKind, CellKind? memberKind, IReadOnlyList<CellKind> argumentKinds) {
            var signature = $"{subprogram}:{kind}:{resultKind}:{memberKind}:{string.Join(
                separator: ',',
                values: argumentKinds
            )}";

            if (!shared.TryGetValue(
                key: signature,
                value: out var body
            )) {
                var compiled = CompileInstructions(
                    argumentKinds: argumentKinds,
                    calling: Entering(subprogram: subprogram),
                    context: context,
                    expression: expression,
                    instructions: expression.Subprograms[subprogram].Instructions,
                    kind: kind,
                    memberKind: memberKind,
                    result: out var left,
                    resultKind: resultKind,
                    ruleName: ruleName,
                    shared: shared,
                    verb: verb
                );

                body = (compiled, left);
                shared[signature] = body;
            }

            return body;
        }
        CompiledExpressionToken SignOf() {
            Require(
                arity: 1,
                operation: ExpressionOp.Sign
            );
            if (kinds[(depth - 1)] is not (CellKind.Int or CellKind.Fixed)) {
                throw Malformed(detail: "instruction 'Sign' needs a numeric operand");
            }

            kinds[(depth - 1)] = CellKind.Int;

            return new CompiledExpressionToken(Operation: ExpressionOp.Sign);
        }
    }
}
