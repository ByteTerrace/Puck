using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A row trait declaring a board the inverse of a token row: the engine keeps <c>board</c> derived from
/// <c>tokens</c>/<c>codes</c> rather than the world authoring both by hand.</summary>
public sealed class DerivedBoardLawTests {
    private static StateCell Cell(string key, long value) => new(
        Name(value: key),
        CellValue.Int(value: value)
    );
    private static WorldStateRow CodesRow() => new(
        Name(value: "codes"),
        CellKind.Int,
        Cells: [Cell(
                key: "a",
                value: 5
            ), Cell(
                key: "b",
                value: 9
            )]
    );
    private static WorldStateRow DerivedBoardRow(IReadOnlyList<StateCell>? cells = null) => new(
        Name(value: "board"),
        CellKind.Int,
        Domain: new StateDomain.CellsOf(
            "map",
            Empty: -1
        ),
        Inverse: new StateInverse(
            Name(value: "tokens"),
            Name(value: "codes")
        ),
        Cells: (cells ?? [])
    );
    private static WorldDefinition Document(long a, long b) => Fixtures.BuildDocument() with {
        StateRaw = new(
        World: [TokensRow(
                a: a,
                b: b
            ), CodesRow(), DerivedBoardRow()],
        Lattices: [Grid()]
    ),
        Rules = [],
    };
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(
        document.State,
        row
    )!;
    private static LatticeTopology.Grid Grid() => new(
        "map",
        new DocumentVector3(
            x: 0,
            y: 0,
            z: 0
        ),
        1,
        Width: 4,
        Depth: 4
    );
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static WorldStateRow TokensRow(long a, long b) => new(
        Name(value: "tokens"),
        CellKind.Int,
        Cells: [Cell(
                key: "a",
                value: a
            ), Cell(
                key: "b",
                value: b
            )]
    );

    [Fact]
    public void ADirectBoardWriteIsRefused() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            a: 0,
            b: 1
        ));
        WorldEditEcho? echo = null;

        fixture.Server.EchoTap = next => echo = next;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            WorldPrincipal.Console,
            Row: "board",
            Key: "0",
            Value: 3,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        Assert.True(condition: echo.HasValue);
        Assert.True(condition: echo!.Value.Rejected);
        Assert.Contains(
            "derived board",
            echo.Value.Message
        );
        // Control: the board is untouched by the refused write — the derivation still holds.
        Assert.Equal(
            5,
            Find(
                document: fixture.Server.Definition,
                row: "board"
            ).Cells!.Single(predicate: c => (c.Key.Value == "0")).Value.Raw
        );
    }
    [Fact]
    public void ADirectWholeRowRewriteOfAnEstablishedDerivedBoardIsRefused() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            a: 0,
            b: 1
        ));
        WorldEditEcho? echo = null;

        fixture.Server.EchoTap = next => echo = next;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            WorldPrincipal.Console,
            Row: DerivedBoardRow(cells: [Cell(
                    key: "0",
                    value: 3
                )])
        ));
        fixture.Step();

        Assert.True(condition: echo.HasValue);
        Assert.True(condition: echo!.Value.Rejected);
        Assert.Contains(
            "derived board",
            echo.Value.Message
        );
    }
    [Fact]
    public void ATokenWriteReDerivesTheBoard() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            a: 0,
            b: 1
        ));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            WorldPrincipal.Console,
            Row: "tokens",
            Key: "a",
            Value: 2,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        var board = Find(
            document: fixture.Server.Definition,
            row: "board"
        );

        Assert.DoesNotContain(
            collection: board.Cells!,
            filter: c => (c.Key.Value == "0")
        );
        Assert.Equal(
            5,
            board.Cells!.Single(predicate: c => (c.Key.Value == "2")).Value.Raw
        );
        Assert.Equal(
            9,
            board.Cells!.Single(predicate: c => (c.Key.Value == "1")).Value.Raw
        );
    }
    // The board is never authored, only derived, and the derivation the walk checks against is the arena's own
    // recompute: a document carrying board cells its tokens and codes rows do not produce is refused.
    [Fact]
    public void AnAuthoredBoardDisagreeingWithItsDerivationIsRefused() {
        var document = Fixtures.BuildDocument() with {
            StateRaw = new(
            World: [TokensRow(
                    a: 0,
                    b: 1
                ), CodesRow(), DerivedBoardRow(cells: [Cell(
                        key: "0",
                        value: 7
                    ), Cell(
                        key: "1",
                        value: 9
                    )])],
            Lattices: [Grid()]
        ),
            Rules = [],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: document,
            reason: out var reason
        ));
        Assert.Contains(
            expectedSubstring: "the board is never authored, only derived",
            actualString: reason
        );
    }
    [Fact]
    public void InstallDerivesTheBoardFromTokensAndCodes() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            a: 0,
            b: 1
        ));

        var board = Find(
            document: fixture.Server.Definition,
            row: "board"
        );

        Assert.Equal(
            5,
            board.Cells!.Single(predicate: c => (c.Key.Value == "0")).Value.Raw
        );
        Assert.Equal(
            9,
            board.Cells!.Single(predicate: c => (c.Key.Value == "1")).Value.Raw
        );
        Assert.DoesNotContain(
            collection: board.Cells!,
            filter: c => (c.Key.Value == "2")
        );
    }
    [Fact]
    public void TheHashDiffersWhenTheInverseTraitDiffersEvenAtIdenticalValues() {
        var plain = Fixtures.BuildDocument() with {
            StateRaw = new(
            World: [TokensRow(
                    a: 0,
                    b: 1
                ), CodesRow(), new WorldStateRow(
                    Name(value: "board"),
                    CellKind.Int,
                    Domain: new StateDomain.CellsOf(
                        "map",
                        Empty: -1
                    ),
                    Cells: [Cell(
                            key: "0",
                            value: 5
                        ), Cell(
                            key: "1",
                            value: 9
                        )]
                )],
            Lattices: [Grid()]
        ),
            Rules = [],
        };
        var derived = Document(
            a: 0,
            b: 1
        );

        using var plainFixture = Fixtures.FreshServer(definition: plain);
        using var derivedFixture = Fixtures.FreshServer(definition: derived);

        // Identical resolved cell values on both boards; only the declared trait differs.
        Assert.Equal(
            Find(
                document: plainFixture.Server.Definition,
                row: "board"
            ).Cells,
            Find(
                document: derivedFixture.Server.Definition,
                row: "board"
            ).Cells
        );
        Assert.NotEqual(
            WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.World,
                server: plainFixture.Server,
                tick: 0UL
            ),
            WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.World,
                server: derivedFixture.Server,
                tick: 0UL
            )
        );
    }
    [Fact]
    public void TwoTokensOnOneCellLetsTheLaterTokenInRowOrderWin() {
        using var fixture = Fixtures.FreshServer(definition: Document(
            a: 0,
            b: 1
        ));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            WorldPrincipal.Console,
            Row: "tokens",
            Key: "a",
            Value: 3,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            WorldPrincipal.Console,
            Row: "tokens",
            Key: "b",
            Value: 3,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        var board = Find(
            document: fixture.Server.Definition,
            row: "board"
        );

        // "b" sits later than "a" in the tokens row's own cell order (declared second) and so wins cell 3 — "a"'s
        // code is simply absent from the board, not a collision refusal.
        Assert.Equal(
            9,
            Assert.Single(collection: board.Cells!).Value.Raw
        );
        Assert.Equal(
            "3",
            Assert.Single(collection: board.Cells!).Key.Value
        );
    }
}
