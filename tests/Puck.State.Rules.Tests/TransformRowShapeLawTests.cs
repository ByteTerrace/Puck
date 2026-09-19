using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: <c>sortKeyed</c> and <c>shuffle</c> permute a row whose order is its own — a keyed
/// row or an ordered zone. A board's position is a topology cell and a ring's is a slot, so neither carries an order
/// to permute: the compiler refuses one of those rows where the transform is authored, and the kernel refuses a
/// transform built by hand around the compiler.</summary>
public sealed class TransformRowShapeLawTests {
    private static (ArenaEffectHost Host, RuleCompileContext Context) Arrange() {
        var section = TransformFixture.Section();
        var context = TransformFixture.Context(section: section);

        return (TransformFixture.Host(
            context: context,
            section: section
        ), context);
    }
    private static bool Resolves(RuleCompileContext context, StateTransform transform, out string reason) => RuleCompiler.TryResolveTransform(
        context: context,
        reason: out reason,
        resolved: out _,
        transform: transform
    );

    [InlineData("scores")]
    [InlineData("deck")]
    [Theory]
    public void ASortKeyedOverARowThatCarriesItsOwnOrderResolves(string row) {
        var (_, context) = Arrange();

        Assert.True(condition: Resolves(
            context: context,
            reason: out var reason,
            transform: new StateTransform.SortKeyed(Row: row)
        ), userMessage: reason);
    }
    [InlineData("log")]
    [InlineData("board")]
    [Theory]
    public void ASortKeyedOverARowWithNoOrderOfItsOwnIsRefusedWhereItIsAuthored(string row) {
        var (_, context) = Arrange();

        Assert.False(condition: Resolves(
            context: context,
            reason: out var reason,
            transform: new StateTransform.SortKeyed(Row: row)
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "sortKeyed requires a keyed or ordered numeric row"
        );
    }
    [InlineData("scores")]
    [InlineData("deck")]
    [Theory]
    public void AShuffleOverARowThatCarriesItsOwnOrderResolves(string row) {
        var (_, context) = Arrange();

        Assert.True(condition: Resolves(
            context: context,
            reason: out var reason,
            transform: new StateTransform.Shuffle(
                Draw: "coin",
                Row: row
            )
        ), userMessage: reason);
    }
    [InlineData("log")]
    [InlineData("board")]
    [Theory]
    public void AShuffleOverARowWithNoOrderOfItsOwnIsRefusedWhereItIsAuthored(string row) {
        var (_, context) = Arrange();

        Assert.False(condition: Resolves(
            context: context,
            reason: out var reason,
            transform: new StateTransform.Shuffle(
                Draw: "coin",
                Row: row
            )
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "shuffle requires a keyed or ordered row"
        );
    }
    [InlineData("log")]
    [InlineData("board")]
    [Theory]
    public void AHandBuiltSortKeyedOverSuchARowRefusesByItsOwnShapeCode(string row) {
        var (host, context) = Arrange();

        Assert.False(condition: host.TryTransform(
            binding: ArenaTransformBinding.None,
            moved: out var moved,
            refusal: out var refusal,
            transform: new ArenaTransform.SortKeyed(
                Descending: false,
                RowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: row
                )
            )
        ));
        Assert.False(condition: moved);
        Assert.Equal(
            actual: Assert.IsType<TransformRefusal>(@object: refusal.Code),
            expected: TransformRefusal.SortKeyedShape
        );
    }
    [InlineData("log")]
    [InlineData("board")]
    [Theory]
    public void AHandBuiltShuffleOverSuchARowRefusesByItsOwnShapeCode(string row) {
        var (host, context) = Arrange();

        Assert.False(condition: host.TryTransform(
            binding: ArenaTransformBinding.None,
            moved: out var moved,
            refusal: out var refusal,
            transform: new ArenaTransform.Shuffle(
                DrawRowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: "coin"
                ),
                RowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: row
                )
            )
        ));
        Assert.False(condition: moved);
        Assert.Equal(
            actual: Assert.IsType<TransformRefusal>(@object: refusal.Code),
            expected: TransformRefusal.ShuffleRowShape
        );
    }
}
