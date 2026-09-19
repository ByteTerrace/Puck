using System.Reflection;

using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a capability is a type. A fact names the facet it reads in its own type argument,
/// <see cref="RuleNeeds"/> is the union of those declarations plus what the effect kinds declare,
/// <see cref="RuleNeeds.Admit"/> refuses a host by the name of the first facet it does not serve, and a rule that
/// names no facet schedules on row versions alone. The reader itself carries no door that hands out a facet by
/// type.</summary>
public sealed class ReaderFacetLawTests {
    private static readonly Type[] TypedFactBases = [
        typeof(EffectFact<>),
        typeof(KeyFact<>),
        typeof(OperandFact<>),
    ];

    // Every assembly the library's own closure can reach, so a fact declared in a project that references
    // Puck.State is swept rather than silently outside it.
    private static IEnumerable<Type> VisibleTypes() => AppDomain.CurrentDomain
        .GetAssemblies()
        .Where(predicate: static assembly => (assembly.GetName().Name?.StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: "Puck."
    ) ?? false))
        .SelectMany(selector: static assembly => assembly.GetTypes());
    private static Type? TypedFacetArgument(Type type) {
        for (var current = type.BaseType; (current is not null); current = current.BaseType) {
            if (
                current.IsGenericType &&
                TypedFactBases.Contains(value: current.GetGenericTypeDefinition())
            ) {
                return current.GetGenericArguments()[0];
            }
        }

        return null;
    }

    [Fact]
    public void AdmittingARuleWhoseFacetTheHostLacksRefusesWithTheFacetName() {
        var (_, arena) = ArenaFixture.Build();
        var builder = new RuleNeedsBuilder();

        builder.AddFact(fact: new TopCardOperand());
        builder.AddFact(fact: new TemperatureOperand());

        var needs = builder.Build();

        Assert.False(condition: RuleNeeds.Admit(
            needs: needs,
            reader: new CardReader(arena: arena),
            refusal: out var refusal
        ));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: nameof(IWeatherFacts)
        );
        Assert.DoesNotContain(
            actualString: refusal,
            expectedSubstring: nameof(ICardFacts)
        );
    }
    [Fact]
    public void AdmittingARuleWhoseFacetsTheHostServesSucceedsWithNoRefusal() {
        var (_, arena) = ArenaFixture.Build();
        var builder = new RuleNeedsBuilder();

        builder.AddFact(fact: new TopCardOperand());
        builder.AddFact(fact: new TopCardKeyFact());
        builder.AddFact(fact: new SaveCardEffect());

        Assert.True(condition: RuleNeeds.Admit(
            needs: builder.Build(),
            reader: new CardReader(arena: arena),
            refusal: out var refusal
        ));
        Assert.Equal(
            actual: refusal,
            expected: string.Empty
        );
    }
    [Fact]
    public void ARuleWithNoNeedsSchedulesOnVersionsAlone() {
        var builder = new RuleNeedsBuilder();

        Assert.False(condition: RuleNeeds.None.Volatile);
        Assert.False(condition: RuleNeeds.None.ReadsTick);
        Assert.Empty(collection: RuleNeeds.None.Facets);

        Assert.False(condition: builder.Build().Volatile);

        builder.AddFact(fact: new TopCardOperand());

        Assert.True(condition: builder.Build().Volatile);

        builder.Clear();

        Assert.False(condition: builder.Build().Volatile);

        builder.AddHostOwnedRow(rowOrdinal: ArenaFixture.Field);

        Assert.True(condition: builder.Build().Volatile);
    }
    [Fact]
    public void EveryTypedFactDeclaresItsFacetFromItsTypeArgumentAndNowhereElse() {
        Assert.Same(
            actual: new TopCardOperand().Facet,
            expected: FacetRef.Of<ICardFacts>()
        );
        Assert.Same(
            actual: new TopCardKeyFact().Facet,
            expected: FacetRef.Of<ICardFacts>()
        );
        Assert.Same(
            actual: new SaveCardEffect().Facet,
            expected: FacetRef.Of<ICardFacts>()
        );
        Assert.Same(
            actual: new TemperatureOperand().Facet,
            expected: FacetRef.Of<IWeatherFacts>()
        );
        Assert.Equal(
            actual: FacetRef.Of<ICardFacts>().Name,
            expected: nameof(ICardFacts)
        );
        // The base answers from its type argument and seals the answer, so a case has no member to fill in.
        foreach (var fact in TypedFactBases) {
            var property = fact.GetProperty(name: nameof(IFacetFact.Facet));

            Assert.NotNull(@object: property);
            Assert.True(condition: property!.GetMethod!.IsFinal);
        }
    }
    [Fact]
    public void RuleNeedsIsTheUnionOfItsFactsTypeDeclaredNeeds() {
        var builder = new RuleNeedsBuilder();

        builder.AddFact(fact: new TopCardOperand());
        builder.AddFact(fact: new TopCardKeyFact());
        builder.AddFact(fact: new TemperatureOperand());
        builder.AddFact(fact: new SaveCardEffect());
        builder.AddFact(fact: new StampCardEffect());
        builder.AddHostOwnedRow(rowOrdinal: ArenaFixture.Field);
        builder.AddHostOwnedRow(rowOrdinal: ArenaFixture.Field);

        var needs = builder.Build();

        Assert.Equal(
            actual: needs.Facets.Count,
            expected: 2
        );
        Assert.Same(
            actual: needs.Facets[0],
            expected: FacetRef.Of<ICardFacts>()
        );
        Assert.Same(
            actual: needs.Facets[1],
            expected: FacetRef.Of<IWeatherFacts>()
        );
        Assert.Equal(
            actual: Assert.Single(collection: needs.HostOwnedRows),
            expected: ArenaFixture.Field
        );
        Assert.True(condition: needs.Irreversible);
        Assert.True(condition: needs.ReadsTick);
        Assert.True(condition: needs.Volatile);
    }
    [Fact]
    public void TheReaderAnswersRowVersionsFromTheArenaAndAdvertisesOnlyWhatItImplements() {
        var (_, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: arena.Catalog);
        var mark = arena.BeginScope();
        IStateReader reader = new CardReader(arena: arena);

        Assert.True(condition: reader.Advertises<ICardFacts>());
        Assert.False(condition: reader.Advertises<IWeatherFacts>());
        Assert.Same(
            actual: reader.Catalog,
            expected: arena.Catalog
        );
        Assert.False(condition: reader.TryRowVersion(
            rowOrdinal: arena.Layout.RowCount,
            version: out _
        ));
        Assert.True(condition: reader.TryRowVersion(
            rowOrdinal: ArenaFixture.Score,
            version: out var before
        ));
        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 9L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));

        arena.Commit(mark: mark);

        Assert.True(condition: reader.TryRowVersion(
            rowOrdinal: ArenaFixture.Score,
            version: out var after
        ));
        Assert.Equal(
            actual: after,
            expected: arena.RowVersion(rowOrdinal: ArenaFixture.Score)
        );
        Assert.NotEqual(
            actual: after,
            expected: before
        );
    }
    [Fact]
    public void EveryTypedFactTypeNamesAFacetTheHostAdvertisementVocabularyKnows() {
        var swept = 0;

        foreach (var type in VisibleTypes()) {
            if (
                type.IsAbstract ||
                (TypedFacetArgument(type: type) is not { } facet)
            ) {
                continue;
            }

            swept++;

            Assert.True(
                condition: facet.IsInterface,
                userMessage: $"{type.FullName} names {facet.FullName} as its facet, which is not an interface a host can implement."
            );
            Assert.True(
                condition: typeof(IFacet).IsAssignableFrom(c: facet),
                userMessage: $"{type.FullName} names {facet.FullName} as its facet, which does not derive from IFacet."
            );
            Assert.NotEqual(
                actual: facet,
                expected: typeof(IFacet)
            );
        }

        Assert.NotEqual(
            actual: swept,
            expected: 0
        );
    }
    [Fact]
    public void TheReaderCarriesNoDoorThatHandsOutAFacetByType() {
        foreach (var member in typeof(IStateReader).GetMembers(bindingAttr: BindingFlags.Instance | BindingFlags.Public)) {
            if (member is not MethodInfo { IsGenericMethodDefinition: true } method) {
                continue;
            }

            Assert.Equal(
                actual: method.Name,
                expected: nameof(IStateReader.Advertises)
            );
            Assert.Equal(
                actual: method.ReturnType,
                expected: typeof(bool)
            );
        }
    }
}
