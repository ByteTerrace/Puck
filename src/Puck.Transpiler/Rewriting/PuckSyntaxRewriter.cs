using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Rewriting;

/// <summary>Rewrites a parsed <c>.puck</c> syntax tree into another one.</summary>
/// <remarks>
/// <para>A derived class overrides one or more of the <c>Rewrite*</c> hooks and calls the <c>base</c> method to
/// descend into a node's own children; the base implementations do nothing but descend, so a rewriter that
/// overrides nothing is the identity and answers the tree it was handed.</para>
/// <para>The descent is each node's own <see cref="SyntaxNode.MapChildren"/>, so it reaches every child a node
/// declares, and a node whose children all come back unchanged is kept rather than copied. A changed node is
/// rebuilt with the record <c>with</c> operator, so its <see cref="SyntaxNode.Trivia"/> — the author's comments,
/// blank-line runs, and line breaks — and its span ride along untouched unless a hook replaces them. The walk is
/// over source rather than over a lowered document, so the compile-time layer (<c>let</c>, <c>template</c>,
/// <c>for</c>, <c>import</c>, units) is still source afterwards rather than its expansion.</para>
/// <para>A rewrite is not a parse: it reports nothing and refuses nothing about the source it was handed. It throws
/// <see cref="PuckRewriteException"/> for the two things that are defects in the rewrite itself — a hook that
/// answers a typed position with a node that position cannot hold, and a rewrite that replaces a value while
/// keeping the author's own text beside it (<see cref="LiteralExpressionNode.RawText"/>,
/// <see cref="OperandExpressionNode.Text"/>), which is read in preference to the value.</para>
/// </remarks>
public abstract partial class PuckSyntaxRewriter : ISyntaxMap {
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
    TNode ISyntaxMap.One<TNode>(SyntaxNode parent, TNode child) => this.Within(child: child, parent: parent).Rewritten(node: child);
    // A hook that answers nothing empties an optional position rather than raising.
    TNode? ISyntaxMap.Optional<TNode>(SyntaxNode parent, TNode? child) where TNode : class {
        if ((child is null) || (this.Within(child: child, parent: parent).Dispatch(node: child) is not { } rewritten)) {
            return null;
        }
        return ((rewritten as TNode) ?? throw PuckRewriteException.WrongKind(original: child, position: typeof(TNode), rewritten: rewritten));
    }
    // A hook that answers nothing for an element drops it, with the comments and blank lines that hang off it.
    IReadOnlyList<TNode> ISyntaxMap.Many<TNode>(SyntaxNode parent, IReadOnlyList<TNode> children) {
        List<TNode>? rewritten = null;

        for (var index = 0; (index < children.Count); ++index) {
            var child = children[index];
            var answer = this.Within(child: child, parent: parent).Dispatch(node: child);

            if ((rewritten is null) && ReferenceEquals(objA: answer, objB: child)) {
                continue;
            }
            rewritten ??= [.. children.Take(count: index)];
            if (answer is not null) {
                rewritten.Add(item: ((answer as TNode) ?? throw PuckRewriteException.WrongKind(original: child, position: typeof(TNode), rewritten: answer)));
            }
        }

        return (rewritten ?? children);
    }

    /// <summary>Returns <paramref name="node"/> with every child rewritten.</summary>
    /// <typeparam name="TNode">The node's type in the position it stands in.</typeparam>
    /// <param name="node">The node to descend into.</param>
    /// <returns>The rewritten node; <paramref name="node"/> itself when no child changed.</returns>
    /// <remarks>An operand is rebuilt around its rewritten tree (<see cref="RewriteOperandSyntax"/>) and atoms, so
    /// its text spells what it holds.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
    protected TNode Descend<TNode>(TNode node) where TNode : SyntaxNode {
        ArgumentNullException.ThrowIfNull(argument: node);

        return ((TNode)((node is OperandExpressionNode operand)
            ? operand.Rebuilt(map: this, syntax: ((operand.Syntax is { } syntax) ? this.RewriteOperandSyntax(form: operand.Form, node: syntax) : null))
            : node.MapChildren(map: this)
        ));
    }
    /// <summary>Returns the rewriter that rewrites <paramref name="child"/>, a child of <paramref name="parent"/>.</summary>
    /// <param name="parent">The node being descended into.</param>
    /// <param name="child">The child about to be rewritten.</param>
    /// <returns>This rewriter, unless a derived class rewrites a scope differently from what encloses it.</returns>
    protected virtual PuckSyntaxRewriter Within(SyntaxNode parent, SyntaxNode child) => this;
    /// <summary>Returns <paramref name="document"/> rewritten.</summary>
    /// <param name="document">The document to rewrite.</param>
    /// <returns>The rewritten document.</returns>
    protected virtual DocumentNode RewriteDocument(DocumentNode document) => this.Descend(node: document);
    /// <summary>Returns <paramref name="expression"/> rewritten.</summary>
    /// <param name="expression">The expression to rewrite.</param>
    /// <returns>The rewritten expression.</returns>
    protected virtual ExpressionNode RewriteExpression(ExpressionNode expression) => this.Descend(node: expression);
    /// <summary>Returns <paramref name="node"/> rewritten: an argument, a template parameter, a row reference, a
    /// state modifier or cell entry, a family member, a pile token, an enum member, a record field, or a pattern
    /// symbol.</summary>
    /// <param name="node">The node to rewrite.</param>
    /// <returns>The rewritten node, which must be of a kind the position it stands in admits.</returns>
    protected virtual SyntaxNode RewriteNode(SyntaxNode node) => this.Descend(node: node);
    /// <summary>Returns <paramref name="predicate"/> rewritten.</summary>
    /// <param name="predicate">The predicate to rewrite.</param>
    /// <returns>The rewritten predicate.</returns>
    protected virtual PredicateNode RewritePredicate(PredicateNode predicate) => this.Descend(node: predicate);
    /// <summary>Returns <paramref name="value"/> rewritten.</summary>
    /// <param name="value">The effect right-hand side to rewrite.</param>
    /// <returns>The rewritten right-hand side.</returns>
    protected virtual RhsNode RewriteRhs(RhsNode value) => this.Descend(node: value);
    /// <summary>Returns <paramref name="statement"/> rewritten, or <see langword="null"/> to drop it.</summary>
    /// <param name="statement">The statement to rewrite.</param>
    /// <returns>The rewritten statement, or <see langword="null"/> to remove it from its container.</returns>
    /// <remarks>Answering with <see langword="null"/> is legal wherever the container admits a missing statement: a
    /// statement list, and an optional single position such as a rule scope's <c>when</c> header. Dropping one a
    /// position requires raises <see cref="PuckRewriteException"/>.</remarks>
    protected virtual StatementNode? RewriteStatement(StatementNode statement) => this.Descend(node: statement);

    /// <summary>Returns <paramref name="document"/> rewritten by this rewriter.</summary>
    /// <param name="document">The parsed tree to rewrite; it is not modified.</param>
    /// <returns>The rewritten tree, ready for <c>PuckPrinter</c>.</returns>
    /// <exception cref="PuckRewriteException">A hook answered a typed position with a node that position cannot
    /// hold.</exception>
    public DocumentNode Rewrite(DocumentNode document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        return this.RewriteDocument(document: document);
    }
}
