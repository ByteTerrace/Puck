using System.Reflection;
using Puck.State;
using Puck.Transpiler.Ast;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The one enumeration of the concrete syntax node types the rewrite and walk laws run over, so they cannot
/// disagree about which kinds each covers, and the one seeder that builds a node with every child position
/// filled.</summary>
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
    /// <summary>Builds a node of <paramref name="type"/> whose every syntax-node position, and a one-element list in
    /// every list position, holds a node recorded in <paramref name="planted"/>; a default is taken only for a
    /// position that carries no child.</summary>
    /// <param name="type">The concrete node type.</param>
    /// <param name="planted">Receives each child the node was built with, or <see langword="null"/> to record none.</param>
    /// <returns>The node.</returns>
    public static SyntaxNode Instance(Type type, List<SyntaxNode>? planted) {
        var constructor = type.GetConstructors().Single();

        return ((SyntaxNode)constructor.Invoke(parameters: [.. constructor.GetParameters().Select(selector: parameter => Seed(
            parameter: parameter,
            planted: planted
        ))]));
    }

    private static Type Concrete(Type declared) => (declared switch {
        _ when (declared == typeof(ExpressionNode)) => typeof(LiteralExpressionNode),
        _ when (declared == typeof(PredicateNode)) => typeof(ComparisonPredicateNode),
        _ when (declared == typeof(RhsNode)) => typeof(RhsTextNode),
        _ when (declared == typeof(StatementNode)) => typeof(FlagStatementNode),
        _ when (declared == typeof(TestExpectItemNode)) => typeof(TestExpectationNode),
        _ when (declared == typeof(TestGivenItemNode)) => typeof(TestGivenNode),
        _ when (declared == typeof(TestStepNode)) => typeof(TestTicksStepNode),
        _ => declared,
    });
    private static object? Seed(ParameterInfo parameter, List<SyntaxNode>? planted) {
        var type = parameter.ParameterType;

        if (type == typeof(string)) {
            return "x";
        }
        if (type == typeof(PatternNode)) {
            return new PatternNode.Nothing();
        }
        if (typeof(SyntaxNode).IsAssignableFrom(c: type)) {
            var node = Instance(
                planted: null,
                type: Concrete(declared: type)
            );

            planted?.Add(item: node);

            return node;
        }
        if (
            type.IsGenericType &&
            (type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        ) {
            var element = type.GetGenericArguments()[0];

            if (typeof(SyntaxNode).IsAssignableFrom(c: element)) {
                var array = Array.CreateInstance(
                    elementType: element,
                    length: 1
                );
                var node = Instance(
                    planted: null,
                    type: Concrete(declared: element)
                );

                array.SetValue(
                    index: 0,
                    value: node
                );
                planted?.Add(item: node);

                return array;
            }

            if (element == typeof(InterpolationSegment)) {
                var hole = Instance(
                    planted: null,
                    type: typeof(LiteralExpressionNode)
                );

                planted?.Add(item: hole);

                return new InterpolationSegment[] { new InterpolationSegment.Hole(Expression: ((ExpressionNode)hole)) };
            }

            return Array.CreateInstance(
                elementType: element,
                length: 0
            );
        }
        if (type.IsEnum) {
            return Enum.GetValues(enumType: type).GetValue(index: 0);
        }
        if (parameter.HasDefaultValue) {
            return parameter.DefaultValue;
        }

        return (Activator.CreateInstance(type: type) ?? throw new InvalidOperationException(message: $"no seed for {type.Name}"));
    }
}
