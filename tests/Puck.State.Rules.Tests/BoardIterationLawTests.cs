using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a board row addresses its cells by position like every other shape, so a
/// <c>forEach</c> over one binds each held cell's own topology key and skips the cells the row does not hold.</summary>
public sealed class BoardIterationLawTests {
    [Fact]
    public void ABoardRowAnswersItsHeldCellsKeyByPosition() {
        var section = TransformFixture.Section();
        var context = TransformFixture.Context(section: section);
        var host = TransformFixture.Host(
            context: context,
            section: section
        );
        var arena = host.Arena;
        var ordinal = TransformFixture.Ordinal(
            context: context,
            name: "left"
        );

        Assert.Equal(
            actual: arena.CellCount(rowOrdinal: ordinal),
            expected: 2
        );

        var keys = new List<string>();
        var cursor = 0;

        while (arena.TryNextCell(
            key: out var key,
            cursor: ref cursor,
            rowOrdinal: ordinal
        )) {
            keys.Add(item: arena.Catalog.Keys[key].Value);
        }

        Assert.Equal(
            actual: keys,
            expected: ["0", "2"]
        );
    }
    [Fact]
    public void AForEachOverABoardFiresOncePerHeldCellUnderThatCellsKey() {
        var section = TransformFixture.Section();
        var context = TransformFixture.Context(section: section);
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: [new Rule(
                    Name: TransformFixture.Name(value: "paint"),
                    Effects: [new ActionEffect.AddState(
                            Key: "$each",
                            State: "left",
                            Value: 10m
                        )],
                    ForEach: "left",
                    Mode: ActionTriggerMode.Level
                )]
        );
        var host = TransformFixture.Host(
            context: context,
            section: section
        );
        var evaluator = new RuleEvaluator(host: host);

        Assert.True(condition: evaluator.Evaluate(
            latch: new RuleLatch(),
            rules: compiled,
            stepTicks: 1UL
        ));
        Assert.Equal(
            actual: TransformFixture.BoardListing(
                arena: host.Arena,
                rowOrdinal: TransformFixture.Ordinal(
                    context: context,
                    name: "left"
                )
            ),
            expected: "11,0,11,0"
        );
        Assert.Empty(collection: evaluator.Diagnostics());
    }
}
