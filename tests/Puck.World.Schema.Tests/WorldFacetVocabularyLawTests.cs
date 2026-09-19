using System.Reflection;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="IWorldFacts"/> is a host facet carrying exactly the vocabulary the
/// world's facts read — one <c>Read</c> per world operand fact, the body and pair-key resolutions, and the reads of
/// the rows the world's own storage serves. A pair key resolves to an interned <see cref="CellKey"/> rather than a
/// string.</summary>
public sealed class WorldFacetVocabularyLawTests {
    private static IEnumerable<Type> ReadOperands(Type reader) => reader
        .GetMethods(bindingAttr: BindingFlags.Instance | BindingFlags.Public)
        .Where(predicate: static method => string.Equals(
        a: method.Name,
        b: "Read",
        comparisonType: StringComparison.Ordinal
    ))
        .Select(selector: static method => method.GetParameters()[0].ParameterType)
        .OrderBy(keySelector: static type => type.FullName, comparer: StringComparer.Ordinal);

    [Fact]
    public void TheWorldFacetIsAFacet() {
        Assert.True(condition: typeof(IWorldFacts).IsInterface);
        Assert.True(condition: typeof(IFacet).IsAssignableFrom(c: typeof(IWorldFacts)));
        Assert.Equal(
            actual: FacetRef.Of<IWorldFacts>().Name,
            expected: nameof(IWorldFacts)
        );
    }
    [Fact]
    public void TheWorldFacetReadsExactlyTheOperandsTheWorldsFactsRead() {
        var facet = ReadOperands(reader: typeof(IWorldFacts)).ToArray();

        Assert.NotEmpty(collection: facet);

        foreach (var operand in facet) {
            Assert.True(
                condition: typeof(WorldOperandFact).IsAssignableFrom(c: operand),
                userMessage: $"IWorldFacts reads {operand.FullName}, which is not a world operand fact."
            );
        }
    }
    [Fact]
    public void TheWorldFacetResolvesABodyIndexAndAnInternedPairKey() {
        var pairKey = typeof(IWorldFacts).GetMethod(name: nameof(IWorldFacts.PairKey));
        var resolveBody = typeof(IWorldFacts).GetMethod(name: nameof(IWorldFacts.ResolveBody));

        Assert.NotNull(@object: pairKey);
        Assert.NotNull(@object: resolveBody);
        Assert.Equal(
            actual: pairKey!.ReturnType,
            expected: typeof(CellKey)
        );
        Assert.Equal(
            actual: pairKey.GetParameters()[0].ParameterType,
            expected: typeof(PairKeyFact)
        );
        Assert.Equal(
            actual: resolveBody!.ReturnType,
            expected: typeof(int)
        );
    }
    [Fact]
    public void TheWorldFacetServesTheHostOwnedRowShapesARuleMayRead() {
        Assert.NotNull(@object: typeof(IWorldFacts).GetMethod(name: nameof(IWorldFacts.TryReadHostOwnedCell)));
        Assert.NotNull(@object: typeof(IWorldFacts).GetMethod(name: nameof(IWorldFacts.TryReadHostOwnedSlot)));
    }
}
