using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Rewriting;

/// <summary>Rewrites a parsed <c>.puck</c> syntax tree into another one.</summary>
/// <remarks>
/// <para>A derived class overrides one or more of the <c>Rewrite*</c> hooks and calls the <c>base</c> method to
/// descend into a node's own children; the base implementations do nothing but descend, so a rewriter that
/// overrides nothing is the identity and reprints its input byte for byte.</para>
/// <para>Every node is rebuilt with the record <c>with</c> operator, so a node's <see cref="SyntaxNode.Trivia"/> —
/// the author's comments, blank-line runs, and line breaks — and its span ride along untouched unless a hook
/// replaces them. The walk is over source rather than over a lowered document, so the compile-time layer
/// (<c>let</c>, <c>template</c>, <c>for</c>, <c>import</c>, units, <c>sql { }</c>) is still source afterwards
/// rather than its expansion.</para>
/// <para>A rewrite is not a parse: it reports nothing and refuses nothing about the source it was handed. It throws
/// <see cref="PuckRewriteException"/> for the three things that are defects in the rewrite itself — a node kind the
/// descent has no arm for, a hook that answers a typed position with a node that position cannot hold, and a
/// rewrite that replaces a value while keeping the author's own text beside it
/// (<see cref="LiteralExpressionNode.RawText"/>, <see cref="OperandExpressionNode.Text"/>), which is read in
/// preference to the value.</para>
/// </remarks>
public abstract partial class PuckSyntaxRewriter {
    // Two members carry the author's own text beside the value it was parsed from, and a consumer prefers the
    // text: the printer prints a numeric literal's RawText, and operand printing reads its
    // Text. A rewrite that replaces the value and keeps the text would be discarded with no trace, or
    // would print one thing and compile to another, so it is refused instead.
    private static void RefuseShadowedText(SyntaxNode original, SyntaxNode? rewritten) {
        if (
            (original is LiteralExpressionNode literal) &&
            (rewritten is LiteralExpressionNode replacement) &&
            (literal.RawText is not null) &&
            string.Equals(
                a: literal.RawText,
                b: replacement.RawText,
                comparisonType: StringComparison.Ordinal
            ) &&
            !Equals(
                objA: literal.Value,
                objB: replacement.Value
            )
        ) {
            throw PuckRewriteException.ShadowedText(
                member: nameof(LiteralExpressionNode.RawText),
                node: original,
                text: literal.RawText
            );
        }
        if ((original is OperandExpressionNode sourceOperand) && (rewritten is OperandExpressionNode changedOperand) &&
            !string.Equals(a: sourceOperand.Text, b: changedOperand.Text, comparisonType: StringComparison.Ordinal) &&
            ReferenceEquals(objA: sourceOperand.Syntax, objB: changedOperand.Syntax)) {
            throw new PuckRewriteException("An operand rewrite changed Text but retained its parsed Syntax; rebuild the operand through PuckParser.CreateOperand or use RewriteOperandSyntax.");
        }
        if ((original is OperandExpressionNode operand) && (rewritten is OperandExpressionNode replacedOperand) &&
            string.Equals(a: operand.Text, b: replacedOperand.Text, comparisonType: StringComparison.Ordinal) &&
            !Equals(objA: operand.Syntax, objB: replacedOperand.Syntax)) {
            throw PuckRewriteException.ShadowedText(member: nameof(OperandExpressionNode.Text), node: original, text: operand.Text);
        }
    }
    // Routes one node through the hook its kind belongs to. The hierarchies are disjoint apart from
    // EffectStatementNode, which is a StatementNode and is matched as one.
    private SyntaxNode? Dispatch(SyntaxNode node) {
        var rewritten = (node switch {
            StatementNode statement => this.RewriteStatement(statement: statement),
            ExpressionNode expression => this.RewriteExpression(expression: expression),
            PredicateNode predicate => this.RewritePredicate(predicate: predicate),
            RhsNode value => this.RewriteRhs(value: value),
            _ => this.RewriteNode(node: node),
        });

        RefuseShadowedText(
            original: node,
            rewritten: rewritten
        );

        return rewritten;
    }
    // Demands the answer back in the position's own type, because the position cannot hold anything else.
    private TNode Rewritten<TNode>(TNode node) where TNode : SyntaxNode {
        var rewritten = this.Dispatch(node: node);

        return ((rewritten is TNode typed)
            ? typed
            : throw PuckRewriteException.WrongKind(
                original: node,
                position: typeof(TNode),
                rewritten: rewritten
            )
        );
    }
    private TNode? RewrittenOrNull<TNode>(TNode? node) where TNode : SyntaxNode => ((node is null)
        ? null
        : this.Rewritten(node: node)
    );
    // An optional single position, where a hook that answers nothing empties the position rather than raising.
    private TNode? RewrittenOrDropped<TNode>(TNode? node) where TNode : SyntaxNode {
        if (node is null) {
            return null;
        }

        var rewritten = this.Dispatch(node: node);

        if (rewritten is null) {
            return null;
        }

        return ((rewritten is TNode typed)
            ? typed
            : throw PuckRewriteException.WrongKind(
                original: node,
                position: typeof(TNode),
                rewritten: rewritten
            )
        );
    }
    private IReadOnlyList<TNode> RewrittenNodes<TNode>(IReadOnlyList<TNode> nodes) where TNode : SyntaxNode {
        var rewritten = new TNode[nodes.Count];

        for (var index = 0; (index < nodes.Count); ++index) {
            rewritten[index] = this.Rewritten(node: nodes[index]);
        }

        return rewritten;
    }
    private IReadOnlyList<TNode>? RewrittenNodesOrNull<TNode>(IReadOnlyList<TNode>? nodes) where TNode : SyntaxNode => ((nodes is null)
        ? null
        : this.RewrittenNodes(nodes: nodes)
    );
    private IReadOnlyList<InterpolationSegment> RewrittenSegments(IReadOnlyList<InterpolationSegment> segments) {
        var rewritten = new InterpolationSegment[segments.Count];

        for (var index = 0; (index < segments.Count); ++index) {
            rewritten[index] = (segments[index] switch {
                InterpolationSegment.Hole hole => new InterpolationSegment.Hole(Expression: this.Rewritten(node: hole.Expression)),
                var literal => literal,
            });
        }

        return rewritten;
    }

    /// <summary>Returns <paramref name="document"/> with every statement rewritten.</summary>
    /// <param name="document">The document to descend into.</param>
    /// <returns>The rewritten document.</returns>
    protected DocumentNode DescendDocument(DocumentNode document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        return (document with { Statements = this.DescendStatements(statements: document.Statements) });
    }
    /// <summary>Returns <paramref name="effect"/> with every child rewritten.</summary>
    /// <param name="effect">The effect statement to descend into.</param>
    /// <returns>The rewritten effect statement.</returns>
    /// <exception cref="PuckRewriteException">The descent has no arm for <paramref name="effect"/>'s kind.</exception>
    protected EffectStatementNode DescendEffect(EffectStatementNode effect) => (effect switch {
        AddCellStatementNode add => (add with { Rhs = this.Rewritten(node: add.Rhs), Target = this.Rewritten(node: add.Target) }),
        BreakStatementNode leaf => leaf,
        ClaimStatementNode claim => (claim with { Body = this.DescendStatements(statements: claim.Body) }),
        ClaimPairStatementNode claim => (claim with { Body = this.DescendStatements(statements: claim.Body) }),
        CompoundAssignStatementNode compound => (compound with { Rhs = this.Rewritten(node: compound.Rhs), Target = this.Rewritten(node: compound.Target) }),
        DealStatementNode leaf => leaf,
        DrawStatementNode leaf => leaf,
        IfStatementNode branch => (branch with {
            Condition = this.Rewritten(node: branch.Condition),
            Else = this.DescendStatementsOrNull(statements: branch.Else),
            Then = this.DescendStatements(statements: branch.Then),
        }),
        PushStatementNode push => (push with { Rhs = this.Rewritten(node: push.Rhs) }),
        PoolForEachStatementNode each => (each with { Body = this.DescendStatements(statements: each.Body) }),
        ReleaseStatementNode release => release,
        RemoveCellStatementNode remove => (remove with { Target = this.Rewritten(node: remove.Target) }),
        RepeatStatementNode repeat => (repeat with { Body = this.DescendStatements(statements: repeat.Body), Count = this.Rewritten(node: repeat.Count) }),
        ScheduleStatementNode schedule => (schedule with { Target = this.Rewritten(node: schedule.Target) }),
        SetCellStatementNode set => (set with { Rhs = this.Rewritten(node: set.Rhs), Target = this.Rewritten(node: set.Target) }),
        ShuffleStatementNode leaf => leaf,
        TransactionStatementNode transaction => (transaction with {
            MainEffects = this.DescendStatements(statements: transaction.MainEffects),
            OnFailureEffects = this.DescendStatementsOrNull(statements: transaction.OnFailureEffects),
        }),
        TransformStatementNode transform => (transform with { Transform = this.Rewritten(node: transform.Transform) }),
        _ => throw PuckRewriteException.UnknownKind(node: effect),
    });
    /// <summary>Returns <paramref name="expression"/> with every child rewritten.</summary>
    /// <param name="expression">The expression to descend into.</param>
    /// <returns>The rewritten expression.</returns>
    /// <exception cref="PuckRewriteException">The descent has no arm for <paramref name="expression"/>'s kind.</exception>
    protected ExpressionNode DescendExpression(ExpressionNode expression) => (expression switch {
        ArrayExpressionNode array => (array with { Elements = this.RewrittenNodes(nodes: array.Elements) }),
        AssetExpressionNode leaf => leaf,
        BinaryExpressionNode binary => (binary with { Left = this.Rewritten(node: binary.Left), Right = this.Rewritten(node: binary.Right) }),
        CallExpressionNode call => (call with { Arguments = this.RewrittenNodes(nodes: call.Arguments) }),
        ColorExpressionNode leaf => leaf,
        ConditionalExpressionNode conditional => (conditional with {
            Condition = this.Rewritten(node: conditional.Condition),
            WhenFalse = this.Rewritten(node: conditional.WhenFalse),
            WhenTrue = this.Rewritten(node: conditional.WhenTrue),
        }),
        IdentifierExpressionNode leaf => leaf,
        IndexExpressionNode index => (index with { Index = this.Rewritten(node: index.Index), Target = this.Rewritten(node: index.Target) }),
        InterpolatedStringNode interpolated => (interpolated with { Segments = this.RewrittenSegments(segments: interpolated.Segments) }),
        LambdaExpressionNode lambda => (lambda with { Body = this.Rewritten(node: lambda.Body) }),
        LiteralExpressionNode leaf => leaf,
        MemberAccessExpressionNode member => (member with { Target = this.Rewritten(node: member.Target) }),
        ObjectExpressionNode instance => (instance with { Properties = this.RewrittenNodes(nodes: instance.Properties) }),
        OperandExpressionNode operand => this.RewrittenOperand(operand: operand),
        RangeExpressionNode range => (range with { End = this.RewrittenOrNull(node: range.End), Start = this.RewrittenOrNull(node: range.Start) }),
        UnaryExpressionNode unary => (unary with { Operand = this.Rewritten(node: unary.Operand) }),
        _ => throw PuckRewriteException.UnknownKind(node: expression),
    });
    /// <summary>Returns <paramref name="node"/> with every child rewritten.</summary>
    /// <param name="node">The node to descend into: one of the kinds that is neither a statement, an expression, a
    /// predicate, nor an effect right-hand side.</param>
    /// <returns>The rewritten node.</returns>
    /// <exception cref="PuckRewriteException">The descent has no arm for <paramref name="node"/>'s kind.</exception>
    protected SyntaxNode DescendNode(SyntaxNode node) => (node switch {
        ArgumentNode argument => (argument with { Value = this.Rewritten(node: argument.Value) }),
        DocumentNode document => this.DescendDocument(document: document),
        EnumMemberNode leaf => leaf,
        FamilyMemberNode family => (family with { First = this.RewrittenOrNull(node: family.First), Last = this.RewrittenOrNull(node: family.Last) }),
        PatternSymbolDeclarationNode leaf => leaf,
        RecordFieldNode field => (field with {
            Default = this.RewrittenOrNull(node: field.Default),
            Modifiers = this.RewrittenNodes(nodes: field.Modifiers),
        }),
        RowRefNode row => ((row.KeyAtom is { } atom)
            ? this.RewrittenKeyAtom(atom: atom, row: row)
            : row
        ),
        StateCellEntryNode cell => (cell with { Modifiers = this.RewrittenNodes(nodes: cell.Modifiers), Value = this.Rewritten(node: cell.Value) }),
        StateModifierNode modifier => (modifier with { Arguments = this.RewrittenNodes(nodes: modifier.Arguments) }),
        StatePileTokenNode leaf => leaf,
        TemplateParameterNode parameter => (parameter with { DefaultValue = this.RewrittenOrNull(node: parameter.DefaultValue) }),
        TestExpectBlockNode block => (block with { Expectations = this.RewrittenNodes(nodes: block.Expectations) }),
        TestExpectWorldNode addressed => (addressed with { Expectations = this.RewrittenNodes(nodes: addressed.Expectations) }),
        TestExpectationNode expectation => (expectation with { Predicate = this.Rewritten(node: expectation.Predicate) }),
        TestGivenBlockNode block => (block with { Cells = this.RewrittenNodes(nodes: block.Cells) }),
        TestGivenNode cell => (cell with { Target = this.Rewritten(node: cell.Target), Value = this.Rewritten(node: cell.Value) }),
        TestGivenWorldNode addressed => (addressed with { Cells = this.RewrittenNodes(nodes: addressed.Cells) }),
        TestSeatStepNode leaf => leaf,
        TestTicksStepNode leaf => leaf,
        TestWhenBlockNode block => (block with { Steps = this.RewrittenNodes(nodes: block.Steps) }),
        TestWhenWorldNode addressed => (addressed with { Steps = this.RewrittenNodes(nodes: addressed.Steps) }),
        _ => throw PuckRewriteException.UnknownKind(node: node),
    });
    /// <summary>Returns <paramref name="predicate"/> with every child rewritten.</summary>
    /// <param name="predicate">The predicate to descend into.</param>
    /// <returns>The rewritten predicate.</returns>
    /// <exception cref="PuckRewriteException">The descent has no arm for <paramref name="predicate"/>'s kind.</exception>
    protected PredicateNode DescendPredicate(PredicateNode predicate) => (predicate switch {
        AndPredicateNode conjunction => (conjunction with { Operands = this.RewrittenNodes(nodes: conjunction.Operands) }),
        CallPredicateNode gate => (gate with { Call = this.Rewritten(node: gate.Call) }),
        ComparisonPredicateNode comparison => comparison with { Left = this.Rewritten(node: comparison.Left), Right = this.Rewritten(node: comparison.Right) },
        NotPredicateNode negation => (negation with { Operand = this.Rewritten(node: negation.Operand) }),
        OrPredicateNode disjunction => (disjunction with { Operands = this.RewrittenNodes(nodes: disjunction.Operands) }),
        _ => throw PuckRewriteException.UnknownKind(node: predicate),
    });
    /// <summary>Returns <paramref name="value"/> with every child rewritten.</summary>
    /// <param name="value">The effect right-hand side to descend into.</param>
    /// <returns>The rewritten right-hand side.</returns>
    /// <exception cref="PuckRewriteException">The descent has no arm for <paramref name="value"/>'s kind.</exception>
    protected RhsNode DescendRhs(RhsNode value) => (value switch {
        RhsOperandNode operand => operand with { Expression = this.Rewritten(node: operand.Expression) },
        RhsSecondsNode leaf => leaf,
        RhsTextNode leaf => leaf,
        _ => throw PuckRewriteException.UnknownKind(node: value),
    });
    /// <summary>Returns <paramref name="statement"/> with every child rewritten.</summary>
    /// <param name="statement">The statement to descend into.</param>
    /// <returns>The rewritten statement.</returns>
    /// <exception cref="PuckRewriteException">The descent has no arm for <paramref name="statement"/>'s kind.</exception>
    protected StatementNode DescendStatement(StatementNode statement) => (statement switch {
        AddonMemoryWatchNode leaf => leaf,
        AddonRequestNode leaf => leaf,
        BlockNode block => (block with {
            NameExpression = this.RewrittenOrNull(node: block.NameExpression),
            Statements = this.DescendStatements(statements: block.Statements),
        }),
        CellSetDeclarationNode leaf => leaf,
        DecisionBlockNode decision => (decision with { Statements = this.DescendStatements(statements: decision.Statements) }),
        DerivedStateNode derived => (derived with { Expression = this.Rewritten(node: derived.Expression) }),
        EffectStatementNode effect => this.DescendEffect(effect: effect),
        EmbeddedBlockNode leaf => leaf,
        EnumDeclarationNode declaration => (declaration with { Members = this.RewrittenNodes(nodes: declaration.Members) }),
        ErrorStatementNode leaf => leaf,
        ExportNode leaf => leaf,
        ExpressionStatementNode expression => (expression with { Expression = this.Rewritten(node: expression.Expression) }),
        FlagStatementNode leaf => leaf,
        ForStatementNode loop => (loop with { Body = this.DescendStatements(statements: loop.Body), Sequence = this.Rewritten(node: loop.Sequence) }),
        GraphParameterNode parameter => (parameter with { Value = this.Rewritten(node: parameter.Value) }),
        ImportNode leaf => leaf,
        InterruptStatementNode interrupt => (interrupt with { Predicate = this.Rewritten(node: interrupt.Predicate) }),
        LetNode declaration => (declaration with { Value = this.Rewritten(node: declaration.Value) }),
        LocalStatementNode local => local with { Expression = this.Rewritten(node: local.Expression), NameExpression = this.RewrittenOrNull(node: local.NameExpression) },
        OnNoChoiceBlockNode fallback => (fallback with { Effects = this.DescendStatements(statements: fallback.Effects) }),
        OptionBlockNode option => (option with { Statements = this.DescendStatements(statements: option.Statements) }),
        PatternDeclarationNode declaration => (declaration with { Symbols = this.RewrittenNodes(nodes: declaration.Symbols), Value = this.RewrittenOrNull(node: declaration.Value) }),
        PropertyNode property => (property with { Value = this.Rewritten(node: property.Value) }),
        RecordDeclarationNode declaration => (declaration with { Fields = this.RewrittenNodes(nodes: declaration.Fields) }),
        StatePoolDeclarationNode pool => (pool with {
            Capacity = this.RewrittenOrNull(node: pool.Capacity),
            Initializer = this.RewrittenOrNull(node: pool.Initializer),
        }),
        StatePairPoolDeclarationNode pool => (pool with { MaxLive = this.Rewritten(node: pool.MaxLive) }),
        RuleBlockNode rule => (rule with {
            NameExpression = this.RewrittenOrNull(node: rule.NameExpression),
            Statements = this.DescendStatements(statements: rule.Statements),
        }),
        RuleScopeNode scope => (scope with {
            HeaderWhen = this.RewrittenOrDropped(node: scope.HeaderWhen),
            Statements = this.DescendStatements(statements: scope.Statements),
        }),
        ScoreStatementNode score => score with { Expression = this.Rewritten(node: score.Expression) },
        StabilizeGroupNode group => (group with {
            Undo = this.RewrittenOrNull(node: group.Undo),
            MaxPasses = this.RewrittenOrNull(node: group.MaxPasses),
            Statements = this.DescendStatements(statements: group.Statements),
            UntilCondition = this.RewrittenOrNull(node: group.UntilCondition),
        }),
        StateGridDeclarationNode grid => (grid with {
            Cells = this.RewrittenNodes(nodes: grid.Cells),
            FamilyMembers = this.RewrittenNodesOrNull(nodes: grid.FamilyMembers),
            FamilySize = this.RewrittenOrNull(node: grid.FamilySize),
            Modifiers = this.RewrittenNodes(nodes: grid.Modifiers),
        }),
        StatePileDeclarationNode pile => (pile with {
            FamilyMembers = this.RewrittenNodesOrNull(nodes: pile.FamilyMembers),
            FamilySize = this.RewrittenOrNull(node: pile.FamilySize),
            Initializer = this.RewrittenOrNull(node: pile.Initializer),
            Modifiers = this.RewrittenNodes(nodes: pile.Modifiers),
            Tokens = this.RewrittenNodes(nodes: pile.Tokens),
        }),
        StateSlotDeclarationNode slot => (slot with {
            FamilyMembers = this.RewrittenNodesOrNull(nodes: slot.FamilyMembers),
            FamilySize = this.RewrittenOrNull(node: slot.FamilySize),
            Modifiers = this.RewrittenNodes(nodes: slot.Modifiers),
            Value = this.RewrittenOrNull(node: slot.Value),
        }),
        StateTableDeclarationNode table => (table with {
            Cells = this.RewrittenNodes(nodes: table.Cells),
            FamilyMembers = this.RewrittenNodesOrNull(nodes: table.FamilyMembers),
            FamilySize = this.RewrittenOrNull(node: table.FamilySize),
            Initializer = this.RewrittenOrNull(node: table.Initializer),
            Modifiers = this.RewrittenNodes(nodes: table.Modifiers),
        }),
        TemplateNode template => (template with {
            Body = this.Rewritten(node: template.Body),
            Parameters = this.RewrittenNodes(nodes: template.Parameters),
        }),
        TestDeclarationNode declaration => (declaration with {
            Expect = this.RewrittenOrNull(node: declaration.Expect),
            Given = this.RewrittenOrNull(node: declaration.Given),
            Subject = this.RewrittenOrNull(node: declaration.Subject),
            When = this.RewrittenOrNull(node: declaration.When),
        }),
        WhenStatementNode gate => (gate with { Predicate = this.Rewritten(node: gate.Predicate) }),
        WorkflowNode workflow => (workflow with { Steps = this.RewrittenNodes(nodes: workflow.Steps), Undo = this.RewrittenOrNull(node: workflow.Undo) }),
        WorkflowStepNode step => (step with {
            ForEachCollection = this.RewrittenOrNull(node: step.ForEachCollection),
            Statements = this.DescendStatements(statements: step.Statements),
            UntilCondition = this.RewrittenOrNull(node: step.UntilCondition),
        }),
        WorldDeclarationNode world => (world with { Name = this.Rewritten(node: world.Name), Module = this.Rewritten(node: world.Module) }),
        WorldLinkNode link => (link with {
            Left = this.Rewritten(node: link.Left),
            Properties = this.DescendStatements(statements: link.Properties),
            Right = this.Rewritten(node: link.Right),
        }),
        _ => throw PuckRewriteException.UnknownKind(node: statement),
    });
    /// <summary>Returns <paramref name="statements"/> with every statement rewritten, dropping the ones a hook
    /// answered with nothing.</summary>
    /// <param name="statements">The statements to rewrite, in source order.</param>
    /// <returns>The rewritten statements, in source order.</returns>
    /// <remarks>A dropped statement takes its own leading comments and blank lines with it, because they hang off
    /// the node rather than off its container. A rewrite that means to keep them moves them onto the statement it
    /// keeps.</remarks>
    protected IReadOnlyList<StatementNode> DescendStatements(IReadOnlyList<StatementNode> statements) {
        ArgumentNullException.ThrowIfNull(argument: statements);

        var rewritten = new List<StatementNode>(capacity: statements.Count);

        foreach (var statement in statements) {
            if (this.RewriteStatement(statement: statement) is { } kept) {
                rewritten.Add(item: kept);
            }
        }

        return rewritten;
    }
    /// <summary>Returns <paramref name="statements"/> rewritten, or <see langword="null"/> when there was no body.</summary>
    /// <param name="statements">The statements to rewrite, or <see langword="null"/>.</param>
    /// <returns>The rewritten statements, or <see langword="null"/>.</returns>
    /// <remarks>An absent body and an empty one differ: an <c>onFailure { }</c> that was written empty prints, and
    /// one that was never written does not.</remarks>
    protected IReadOnlyList<StatementNode>? DescendStatementsOrNull(IReadOnlyList<StatementNode>? statements) => ((statements is null)
        ? null
        : this.DescendStatements(statements: statements)
    );
    /// <summary>Returns <paramref name="document"/> rewritten.</summary>
    /// <param name="document">The document to rewrite.</param>
    /// <returns>The rewritten document.</returns>
    protected virtual DocumentNode RewriteDocument(DocumentNode document) => this.DescendDocument(document: document);
    /// <summary>Returns <paramref name="expression"/> rewritten.</summary>
    /// <param name="expression">The expression to rewrite.</param>
    /// <returns>The rewritten expression.</returns>
    protected virtual ExpressionNode RewriteExpression(ExpressionNode expression) => this.DescendExpression(expression: expression);
    /// <summary>Returns <paramref name="node"/> rewritten: an argument, a template parameter, a row reference, a
    /// state modifier or cell entry, a family member, a pile token, an enum member, a record field, or a pattern
    /// symbol.</summary>
    /// <param name="node">The node to rewrite.</param>
    /// <returns>The rewritten node, which must be of a kind the position it stands in admits.</returns>
    protected virtual SyntaxNode RewriteNode(SyntaxNode node) => this.DescendNode(node: node);
    /// <summary>Returns <paramref name="predicate"/> rewritten.</summary>
    /// <param name="predicate">The predicate to rewrite.</param>
    /// <returns>The rewritten predicate.</returns>
    protected virtual PredicateNode RewritePredicate(PredicateNode predicate) => this.DescendPredicate(predicate: predicate);
    /// <summary>Returns <paramref name="value"/> rewritten.</summary>
    /// <param name="value">The effect right-hand side to rewrite.</param>
    /// <returns>The rewritten right-hand side.</returns>
    protected virtual RhsNode RewriteRhs(RhsNode value) => this.DescendRhs(value: value);
    /// <summary>Returns <paramref name="statement"/> rewritten, or <see langword="null"/> to drop it.</summary>
    /// <param name="statement">The statement to rewrite.</param>
    /// <returns>The rewritten statement, or <see langword="null"/> to remove it from its container.</returns>
    /// <remarks>Answering with <see langword="null"/> is legal wherever the container admits a missing statement: a
    /// statement list, and an optional single position such as a rule scope's <c>when</c> header. Dropping one a
    /// position requires raises <see cref="PuckRewriteException"/>.</remarks>
    protected virtual StatementNode? RewriteStatement(StatementNode statement) => this.DescendStatement(statement: statement);

    /// <summary>Returns <paramref name="document"/> rewritten by this rewriter.</summary>
    /// <param name="document">The parsed tree to rewrite; it is not modified.</param>
    /// <returns>The rewritten tree, ready for <c>PuckPrinter</c>.</returns>
    /// <exception cref="PuckRewriteException">The tree carries a node kind the descent has no arm for, or a hook
    /// answered a typed position with a node that position cannot hold.</exception>
    public DocumentNode Rewrite(DocumentNode document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        return this.RewriteDocument(document: document);
    }
}
