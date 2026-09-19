using Puck.Transpiler.Ast;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The one enumeration of the concrete syntax node types both rewrite laws run over, so they cannot
/// disagree about which kinds each covers.</summary>
/// <remarks>Nested public types count: a node declared inside another type is still a node the tree can carry, and
/// <c>Type.IsPublic</c> alone is false for one.</remarks>
internal static class SyntaxNodeKinds {
    /// <summary>Returns every concrete syntax node type the tree can carry, in ordinal name order.</summary>
    /// <returns>The node types.</returns>
    public static IReadOnlyList<Type> All() => [.. typeof(SyntaxNode).Assembly.GetTypes()
        .Where(predicate: static type => (
            !type.IsAbstract &&
            (type.IsPublic || type.IsNestedPublic) &&
            typeof(SyntaxNode).IsAssignableFrom(c: type)
        ))
        .OrderBy(keySelector: static type => type.FullName, comparer: StringComparer.Ordinal)];

    /// <summary>Returns the node types as xUnit theory data, keyed by full name.</summary>
    /// <returns>The node types as theory data.</returns>
    public static TheoryData<string> Names() => new(values: All().Select(selector: static type => type.FullName!));
}
