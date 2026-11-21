namespace Puck.Transpiler.Ast;

/// <summary>The search for the nodes a source position stands in, over the children each node names
/// (<see cref="SyntaxNode.Children"/>).</summary>
public static class SyntaxWalk {
    /// <summary>Returns the nodes the source position <paramref name="offset"/> stands in, from
    /// <paramref name="root"/> down to the innermost.</summary>
    /// <param name="root">The node to search from, usually a document.</param>
    /// <param name="offset">The 0-based source offset.</param>
    /// <returns>The path, outermost first; empty when <paramref name="root"/> does not span
    /// <paramref name="offset"/>.</returns>
    /// <remarks>A node spans the offsets from its first character up to, but not including, the one after its last.
    /// The search descends into the first child, in <see cref="SyntaxNode.Children"/> order, that spans the offset and does not
    /// start before its parent; a child that does was parsed from its own text rather than placed in the source, so it
    /// is never on a path.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<SyntaxNode> PathAt(SyntaxNode root, int offset) {
        ArgumentNullException.ThrowIfNull(argument: root);

        var path = new List<SyntaxNode>();

        if (!Spans(node: root, offset: offset)) {
            return path;
        }

        for (var node = root; (node is not null);) {
            path.Add(item: node);

            var parent = node;

            node = parent.Children().FirstOrDefault(predicate: child => ((child.Offset >= parent.Offset) && Spans(node: child, offset: offset)));
        }

        return path;
    }

    /// <summary>Returns whether <paramref name="offset"/> falls within <paramref name="node"/>'s source span.</summary>
    /// <param name="node">The syntax node to test.</param>
    /// <param name="offset">The 0-based character offset.</param>
    /// <returns><see langword="true"/> when <paramref name="offset"/> lies in <c>[node.Offset, node.Offset + node.Length)</c>.</returns>
    public static bool Spans(SyntaxNode node, int offset) => ((offset >= node.Offset) && (offset < (node.Offset + node.Length)));
}
