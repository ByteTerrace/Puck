namespace Puck.Transpiler.Ast;

/// <summary>What a rebuild answers for each child position of a node: <see cref="SyntaxNode.MapChildren"/> hands
/// every child it holds to one of these, with the node that holds it, and builds the node around the answers.</summary>
/// <remarks>An answer that is the child itself, or the list itself, tells the node nothing changed there; a node
/// whose every position answers so is returned as it stands, so an unchanged subtree is shared rather than
/// copied.</remarks>
public interface ISyntaxMap {
    /// <summary>Answers a position that holds exactly one node.</summary>
    /// <typeparam name="TNode">The node type the position admits.</typeparam>
    /// <param name="parent">The node that holds <paramref name="child"/>.</param>
    /// <param name="child">The node in the position.</param>
    /// <returns>The node the position holds afterwards.</returns>
    TNode One<TNode>(SyntaxNode parent, TNode child) where TNode : SyntaxNode;
    /// <summary>Answers a position that holds one node or nothing.</summary>
    /// <typeparam name="TNode">The node type the position admits.</typeparam>
    /// <param name="parent">The node that holds <paramref name="child"/>.</param>
    /// <param name="child">The node in the position, or <see langword="null"/> when it is empty.</param>
    /// <returns>The node the position holds afterwards, or <see langword="null"/> to leave it empty.</returns>
    TNode? Optional<TNode>(SyntaxNode parent, TNode? child) where TNode : SyntaxNode;
    /// <summary>Answers a position that holds a list of nodes.</summary>
    /// <typeparam name="TNode">The node type the list admits.</typeparam>
    /// <param name="parent">The node that holds <paramref name="children"/>.</param>
    /// <param name="children">The nodes in the position, in order.</param>
    /// <returns>The list the position holds afterwards; <paramref name="children"/> itself when no element
    /// changed.</returns>
    IReadOnlyList<TNode> Many<TNode>(SyntaxNode parent, IReadOnlyList<TNode> children) where TNode : SyntaxNode;
}
/// <summary>Marks a property that holds syntax nodes written about a node rather than inside it, so the node's
/// <see cref="SyntaxNode.Children"/> and <see cref="SyntaxNode.MapChildren"/> leave it out.</summary>
[AttributeUsage(validOn: AttributeTargets.Property)]
public sealed class SyntaxAnnotationAttribute : Attribute;
