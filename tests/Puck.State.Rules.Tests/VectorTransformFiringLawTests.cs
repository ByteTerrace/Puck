using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: the four vector transforms apply over a <see cref="StateArena"/> through the same
/// entry point the eleven scalar ones do, and report what they moved. A <c>remember</c> the table already holds a
/// near duplicate of is a success that writes nothing, and says so.</summary>
public sealed class VectorTransformFiringLawTests {
    private static readonly sbyte[] Components = BuildComponents();

    private static sbyte[] BuildComponents() {
        var components = new sbyte[16];

        components[0] = 127;

        return components;
    }
    private static (ArenaEffectHost Host, RuleCompileContext Context) Arrange() {
        var section = VectorFixture.Section();
        var context = new RuleCompileContext(
            catalog: StateCatalog.Compile(section: section),
            generators: null,
            patterns: null,
            section: section,
            simulationRateHz: 30,
            tables: null,
            vocabulary: RuleVocabulary.Core
        );
        var host = new ArenaEffectHost(
            arena: new StateArena(
                catalog: context.Catalog,
                options: null,
                section: section,
                time: ArenaTime.Origin
            ),
            generators: null
        );

        Assert.True(condition: host.Arena.TryWriteVector(
            components: Components,
            key: host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            reason: out var reason,
            rowOrdinal: RuleCompiler.ResolveRowOrdinal(
                context: context,
                name: "wide"
            )
        ), userMessage: reason);

        return (host, context);
    }
    private static ArenaTransform Resolve(RuleCompileContext context, StateTransform transform) {
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: RulesFixture.Rule(
                effects: [new ActionEffect.TransformState(Transform: transform)],
                name: "vectors"
            )
        );

        Assert.True(condition: ArenaTransforms.TryVectorTransform(
            catalog: context.Catalog,
            effect: compiled.Effects[0],
            reader: null,
            reason: out var reason,
            transform: out var resolved
        ), userMessage: reason);

        return resolved!;
    }
    private static bool Fire(ArenaEffectHost host, RuleCompileContext context, StateTransform transform, out bool moved) {
        var applied = host.TryTransform(
            binding: ArenaTransformBinding.None,
            moved: out moved,
            refusal: out var refusal,
            transform: Resolve(
                context: context,
                transform: transform
            )
        );

        Assert.True(
            condition: applied,
            userMessage: refusal.Reason
        );

        return applied;
    }

    // A mix past the term ceiling is refused whole by the kernel's own code, never mixed from the terms that fit:
    // a hand-built transform is the one caller no compiler has already held to the ceiling.
    [Fact]
    public void AMixPastTheTermCeilingIsRefusedRatherThanTruncated() {
        var (host, context) = Arrange();
        var wide = RuleCompiler.ResolveRowOrdinal(
            context: context,
            name: "wide"
        );
        var slot = host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey);
        var terms = new ArenaMixTerm[(StateCapacity.MaxMixTerms + 1)];

        Array.Fill(
            array: terms,
            value: new ArenaMixTerm(
                Source: VectorSource.Literal(components: Components),
                Weight: 1
            )
        );

        var before = host.Arena.ComputeHash();

        Assert.False(condition: host.TryTransform(
            binding: ArenaTransformBinding.None,
            moved: out var moved,
            refusal: out var refusal,
            transform: new ArenaTransform.Mix(
                IntoKey: slot,
                IntoRowOrdinal: wide,
                Terms: terms
            )
        ));
        Assert.False(condition: moved);
        Assert.True(condition: refusal.IsRefused);
        Assert.Equal(
            actual: host.Arena.ComputeHash(),
            expected: before
        );
    }
    [Fact]
    public void ARememberStoresItsVectorAndSaysItMovedTheTable() {
        var (host, context) = Arrange();
        var bank = RuleCompiler.ResolveRowOrdinal(
            context: context,
            name: "bank"
        );

        _ = Fire(
            context: context,
            host: host,
            moved: out var moved,
            transform: new StateTransform.Remember(
                From: "wide",
                Into: "bank",
                Key: "a",
                UnlessWithin: "0.5"
            )
        );
        Assert.True(condition: moved);
        Assert.Equal(
            actual: host.Arena.CellCount(rowOrdinal: bank),
            expected: 1
        );
    }
    [Fact]
    public void ARememberOfANearDuplicateWritesNothingAndSaysItMovedNothing() {
        var (host, context) = Arrange();
        var bank = RuleCompiler.ResolveRowOrdinal(
            context: context,
            name: "bank"
        );

        _ = Fire(
            context: context,
            host: host,
            moved: out _,
            transform: new StateTransform.Remember(
                From: "wide",
                Into: "bank",
                Key: "a",
                UnlessWithin: "0.5"
            )
        );

        var generation = host.Arena.RowGeneration(rowOrdinal: bank);

        _ = Fire(
            context: context,
            host: host,
            moved: out var moved,
            transform: new StateTransform.Remember(
                From: "wide",
                Into: "bank",
                Key: "b",
                UnlessWithin: "0.5"
            )
        );
        Assert.False(condition: moved);
        Assert.Equal(
            actual: host.Arena.RowGeneration(rowOrdinal: bank),
            expected: generation
        );
        Assert.Equal(
            actual: host.Arena.CellCount(rowOrdinal: bank),
            expected: 1
        );
    }
    [Fact]
    public void AMixAMeanAndANearestWriteTheirDestinations() {
        var (host, context) = Arrange();
        var arena = host.Arena;
        var ranks = RuleCompiler.ResolveRowOrdinal(
            context: context,
            name: "ranks"
        );

        _ = Fire(
            context: context,
            host: host,
            moved: out _,
            transform: new StateTransform.Remember(
                From: "wide",
                Into: "bank",
                Key: "a",
                UnlessWithin: "0.5"
            )
        );
        _ = Fire(
            context: context,
            host: host,
            moved: out var mixed,
            transform: new StateTransform.Mix(
                Into: "wide",
                Terms: [new VectorTerm(
                        From: "bank[a]",
                        Weight: 1
                    )]
            )
        );
        Assert.True(condition: mixed);
        _ = Fire(
            context: context,
            host: host,
            moved: out var meaned,
            transform: new StateTransform.Mean(
                From: "bank",
                Into: "wide"
            )
        );
        Assert.True(condition: meaned);
        _ = Fire(
            context: context,
            host: host,
            moved: out var ranked,
            transform: new StateTransform.Nearest(
                From: "bank",
                Into: "ranks",
                K: 1,
                Query: "wide"
            )
        );
        Assert.True(condition: ranked);
        Assert.Equal(
            actual: arena.CellCount(rowOrdinal: ranks),
            expected: 1
        );
    }
}
