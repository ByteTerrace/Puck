using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Rewriting;

/// <summary>Thrown when a rewrite cannot be carried out: the tree carries a node kind the descent has no arm for,
/// or a hook answered a typed position with a node that position cannot hold.</summary>
/// <remarks>Both are defects in the rewrite rather than refusals about a source, so a caller reports them against
/// the migration and never against the file it was applied to.</remarks>
public sealed class PuckRewriteException : Exception {
    /// <summary>Initializes a new instance of the <see cref="PuckRewriteException"/> class.</summary>
    /// <param name="message">The message describing what the rewrite could not do.</param>
    /// <param name="innerException">The optional inner exception.</param>
    public PuckRewriteException(string message, Exception? innerException = null)
        : base(
        message: message,
        innerException: innerException
    ) {
    }

    /// <summary>Returns the refusal for a node kind <see cref="PuckSyntaxRewriter"/>'s descent has no arm for.</summary>
    /// <param name="node">The node the descent reached.</param>
    /// <returns>The exception to throw.</returns>
    /// <remarks>A new syntax node type raises this on its first rewrite rather than passing through with its own
    /// children unvisited, so the rewriter cannot silently fall behind the tree it walks.</remarks>
    public static PuckRewriteException UnknownKind(SyntaxNode node) {
        ArgumentNullException.ThrowIfNull(argument: node);

        return new PuckRewriteException(
            message: $"PuckSyntaxRewriter has no descent arm for '{node.GetType().Name}' at line {node.Line}, column {node.Column}: add one so the node's own children are rewritten."
        );
    }
    /// <summary>Returns the refusal for a rewrite that replaced a value while keeping the author's own text beside
    /// it, which a consumer reads in preference to the value.</summary>
    /// <param name="node">The node as it stood before the rewrite.</param>
    /// <param name="member">The member holding the author's text.</param>
    /// <param name="text">What that member holds.</param>
    /// <returns>The exception to throw.</returns>
    /// <remarks>Clear the member, or recompute it, in the same <c>with</c> that replaces the value.</remarks>
    public static PuckRewriteException ShadowedText(SyntaxNode node, string member, string text) {
        ArgumentNullException.ThrowIfNull(argument: node);

        return new PuckRewriteException(
            message: $"a rewrite replaced the value of '{node.GetType().Name}' at line {node.Line}, column {node.Column} but kept {member} = '{text}', which is read in preference to the value; clear or recompute {member} in the same rewrite."
        );
    }
    /// <summary>Returns the refusal for a hook that answered a typed position with a node of another kind.</summary>
    /// <param name="original">The node the position held before the rewrite.</param>
    /// <param name="rewritten">What the hook answered with, or <see langword="null"/> when it dropped a node the
    /// position requires.</param>
    /// <param name="position">The node type the position can hold.</param>
    /// <returns>The exception to throw.</returns>
    public static PuckRewriteException WrongKind(SyntaxNode original, SyntaxNode? rewritten, Type position) {
        ArgumentNullException.ThrowIfNull(argument: original);
        ArgumentNullException.ThrowIfNull(argument: position);

        return new PuckRewriteException(
            message: $"a rewrite answered a '{position.Name}' position holding '{original.GetType().Name}' at line {original.Line}, column {original.Column} with '{(rewritten?.GetType().Name ?? "nothing")}'; that position admits only a '{position.Name}'."
        );
    }
}
