using System.Reflection;
using System.Text.Json.Serialization;
using Puck.State;
using Puck.Transpiler;

namespace Puck.World.Transpiler.Tests;

/// <summary>Every construct the world vocabulary registers, read from the registries themselves rather than from a
/// hand-typed list: the polymorphic <c>$type</c> tables in <c>Puck.State</c> that a document's nodes discriminate
/// on, and <see cref="PuckDslVocabulary"/>'s comparison operators and comparison kinds.</summary>
/// <remarks>A name here is <c>family/discriminator</c>. Adding an arm to any registered table adds a name, which
/// the projection law then demands the generator cover — a construct nobody wrote a source for fails by that
/// name rather than passing unnoticed.</remarks>
internal static class ConstructRegistry {
    private static IEnumerable<string> Arms(string family, Type registry) =>
        registry.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
            .Select(selector: attribute => $"{family}/{attribute.TypeDiscriminator}");

    /// <summary>Gets every registered construct name, in ordinal order.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. new[] {
        Arms(family: "effect", registry: typeof(ActionEffect)),
        Arms(family: "predicate", registry: typeof(ActionPredicate)),
        Arms(family: "transform", registry: typeof(StateTransform)),
        Arms(family: "domain", registry: typeof(StateDomain)),
        Arms(family: "topology", registry: typeof(LatticeTopology)),
        Arms(family: "cellSet", registry: typeof(CellSetExpression)),
        Arms(family: "pattern", registry: typeof(PatternNode)),
        Enum.GetValues<ActionStateComparison>().Select(selector: static comparison => $"comparison/{PuckDslVocabulary.NameOf(comparison: comparison)}"),
        PuckDslVocabulary.ComparisonKindNames.Select(selector: static kind => $"comparisonKind/{kind}"),
    }.SelectMany(selector: static names => names).Order(comparer: StringComparer.Ordinal)];
}
