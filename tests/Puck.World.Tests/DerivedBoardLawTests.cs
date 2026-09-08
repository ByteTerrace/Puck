using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A row trait declaring a board the inverse of a token row: the engine keeps <c>board</c> derived from
/// <c>tokens</c>/<c>codes</c> rather than the world authoring both by hand.</summary>
public sealed class DerivedBoardLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateCell Cell(string key, long value) => new(Name(key), value);
    private static LatticeTopology.Grid Grid() => new("map", new DocumentVector3(0, 0, 0), 1, Width: 4, Depth: 4);

    private static WorldStateRow TokensRow(long a, long b) => new(Name("tokens"), CellKind.Int, Cells: [Cell("a", a), Cell("b", b)]);
    private static WorldStateRow CodesRow() => new(Name("codes"), CellKind.Int, Cells: [Cell("a", 5), Cell("b", 9)]);
    private static WorldStateRow DerivedBoardRow(IReadOnlyList<StateCell>? cells = null) => new(
        Name("board"), CellKind.Int,
        Domain: new StateDomain.CellsOf("map", Empty: -1),
        Inverse: new StateInverse(Name("tokens"), Name("codes")),
        Cells: cells ?? []
    );

    private static WorldDefinition Document(long a, long b) => Fixtures.BuildDocument() with {
        StateRaw = new(World: [TokensRow(a, b), CodesRow(), DerivedBoardRow()], Lattices: [Grid()]),
        Rules = [],
    };
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(document.State, row)!;

    [Fact]
    public void InstallDerivesTheBoardFromTokensAndCodes() {
        using var fixture = Fixtures.FreshServer(definition: Document(a: 0, b: 1));

        var board = Find(fixture.Server.Definition, "board");

        Assert.Equal(5, board.Cells!.Single(c => c.Key.Value == "0").Value);
        Assert.Equal(9, board.Cells!.Single(c => c.Key.Value == "1").Value);
        Assert.DoesNotContain(board.Cells!, c => c.Key.Value == "2");
    }

    [Fact]
    public void ATokenWriteReDerivesTheBoard() {
        using var fixture = Fixtures.FreshServer(definition: Document(a: 0, b: 1));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(WorldPrincipal.Console, Row: "tokens", Key: "a", Value: 2, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        var board = Find(fixture.Server.Definition, "board");

        Assert.DoesNotContain(board.Cells!, c => c.Key.Value == "0");
        Assert.Equal(5, board.Cells!.Single(c => c.Key.Value == "2").Value);
        Assert.Equal(9, board.Cells!.Single(c => c.Key.Value == "1").Value);
    }

    [Fact]
    public void ADirectBoardWriteIsRefused() {
        using var fixture = Fixtures.FreshServer(definition: Document(a: 0, b: 1));
        WorldEditEcho? echo = null;
        fixture.Server.EchoTap = next => echo = next;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(WorldPrincipal.Console, Row: "board", Key: "0", Value: 3, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        Assert.True(echo.HasValue);
        Assert.True(echo!.Value.Rejected);
        Assert.Contains("derived board", echo.Value.Message);
        // Control: the board is untouched by the refused write — the derivation still holds.
        Assert.Equal(5, Find(fixture.Server.Definition, "board").Cells!.Single(c => c.Key.Value == "0").Value);
    }

    [Fact]
    public void ADirectWholeRowRewriteOfAnEstablishedDerivedBoardIsRefused() {
        using var fixture = Fixtures.FreshServer(definition: Document(a: 0, b: 1));
        WorldEditEcho? echo = null;
        fixture.Server.EchoTap = next => echo = next;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(WorldPrincipal.Console, Row: DerivedBoardRow(cells: [Cell("0", 3)])));
        fixture.Step();

        Assert.True(echo.HasValue);
        Assert.True(echo!.Value.Rejected);
        Assert.Contains("derived board", echo.Value.Message);
    }

    [Fact]
    public void TwoTokensOnOneCellLetsTheLaterTokenInRowOrderWin() {
        using var fixture = Fixtures.FreshServer(definition: Document(a: 0, b: 1));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(WorldPrincipal.Console, Row: "tokens", Key: "a", Value: 3, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(WorldPrincipal.Console, Row: "tokens", Key: "b", Value: 3, Kind: WorldDocumentWriteKind.Set));
        fixture.Step();

        var board = Find(fixture.Server.Definition, "board");

        // "b" sits later than "a" in the tokens row's own cell order (declared second) and so wins cell 3 — "a"'s
        // code is simply absent from the board, not a collision refusal.
        Assert.Equal(9, Assert.Single(board.Cells!).Value);
        Assert.Equal("3", Assert.Single(board.Cells!).Key.Value);
    }

    [Fact]
    public void TheHashDiffersWhenTheInverseTraitDiffersEvenAtIdenticalValues() {
        var plain = Fixtures.BuildDocument() with {
            StateRaw = new(World: [TokensRow(0, 1), CodesRow(), new WorldStateRow(Name("board"), CellKind.Int, Domain: new StateDomain.CellsOf("map", Empty: -1), Cells: [Cell("0", 5), Cell("1", 9)])], Lattices: [Grid()]),
            Rules = [],
        };
        var derived = Document(a: 0, b: 1);

        using var plainFixture = Fixtures.FreshServer(definition: plain);
        using var derivedFixture = Fixtures.FreshServer(definition: derived);

        // Identical resolved cell values on both boards; only the declared trait differs.
        Assert.Equal(Find(plainFixture.Server.Definition, "board").Cells, Find(derivedFixture.Server.Definition, "board").Cells);
        Assert.NotEqual(
            WorldRuntimeStateHash.Hash(scope: WorldStateHashScope.World, server: plainFixture.Server, tick: 0UL),
            WorldRuntimeStateHash.Hash(scope: WorldStateHashScope.World, server: derivedFixture.Server, tick: 0UL)
        );
    }
}
