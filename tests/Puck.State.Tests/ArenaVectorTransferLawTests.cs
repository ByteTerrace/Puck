using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a transfer between two ordered vector rows carries the member's bytes intact,
/// whether the member leaves from the source's end or from in front of another vector, because the value is
/// copied out before the source compacts.</summary>
public sealed class ArenaVectorTransferLawTests {
    private const int Deck = 1;
    private const int Hand = 2;

    [InlineData("a")]
    [InlineData("b")]
    [Theory]
    public void ATransferredVectorArrivesWithItsBytes(string member) {
        var section = new StateSection(
            Rows: [
                new StateRow(
                    Name: ArenaFixture.Name(value: "tokens"),
                    Kind: CellKind.Int,
                    Capacity: 2,
                    Cells: [
                        new StateCell(Key: ArenaFixture.Name(value: "a"), Value: CellValue.Int(value: 0L)),
                        new StateCell(Key: ArenaFixture.Name(value: "b"), Value: CellValue.Int(value: 0L)),
                    ]
                ),
                new StateRow(
                    Name: ArenaFixture.Name(value: "deck"),
                    Kind: CellKind.Vector,
                    Space: "space8",
                    Capacity: 2,
                    Domain: new StateDomain.KeysOf(
                        ArenaFixture.Name(value: "tokens"),
                        Ordered: true
                    ),
                    Cells: [
                        new StateCell(
                            Key: ArenaFixture.Name(value: "a"),
                            Value: CellValue.Vector(components: ArenaFixture.Unit(axis: 3).Memory)
                        ),
                        new StateCell(
                            Key: ArenaFixture.Name(value: "b"),
                            Value: CellValue.Vector(components: ArenaFixture.Unit(axis: 5).Memory)
                        ),
                    ]
                ),
                new StateRow(
                    Name: ArenaFixture.Name(value: "hand"),
                    Kind: CellKind.Vector,
                    Space: "space8",
                    Capacity: 2,
                    Domain: new StateDomain.KeysOf(
                        ArenaFixture.Name(value: "tokens"),
                        Ordered: true
                    ),
                    Cells: []
                ),
            ],
            Spaces: [new StateSpace(
                Name: ArenaFixture.Name(value: "space8"),
                Model: "model",
                Revision: "r1",
                Dimensions: 8
            )]
        );
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var key = ArenaFixture.Key(
            catalog: catalog,
            value: member
        );

        Assert.True(condition: arena.TryReadVector(
            components: out var before,
            key: key,
            rowOrdinal: Deck
        ));

        var expected = before.ToArray();

        Assert.True(condition: arena.TryTransfer(
            fromOrdinal: Deck,
            insertFirst: false,
            key: key,
            reason: out var reason,
            toOrdinal: Hand
        ), userMessage: reason);
        Assert.True(condition: arena.TryReadVector(
            components: out var after,
            key: key,
            rowOrdinal: Hand
        ));
        Assert.Equal(
            expected: expected,
            actual: after.ToArray()
        );
        Assert.False(condition: arena.TryReadVector(
            components: out _,
            key: key,
            rowOrdinal: Deck
        ));
    }
}
