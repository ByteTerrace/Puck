using Xunit;

namespace Puck.World.Tests;

public sealed class ChessAiLawTests {
    private static WorldDefinition Chess() {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Puck.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        Assert.True(WorldDefinitionLoader.TryLoadFile(Path.Combine(root.FullName, "src/Puck.World/Assets/worlds/games/chess.world.json"), out var world, out var reason), reason);
        return world!;
    }

    private static StateArena Judge(Dictionary<int, int> cells, int mover, int to, int turn = 0, int enPassant = -1, int ply = 1) {
        var world = Chess();
        var codes = WorldDefinitionRows.FindStateRow(world.State, "pieceCode")!.Cells!.ToDictionary(cell => cell.Key.Value, cell => cell.Value.Raw);
        var board = new long[64];
        foreach (var (piece, square) in cells) { board[square] = codes[$"piece{piece}"]; }
        var rows = world.StateRaw!.World!.Select(row => row.Name.Value switch {
            "pieceCell" => row with { Cells = row.Cells!.Select(cell => cell with { Value = CellValue.Int(cells.GetValueOrDefault(int.Parse(cell.Key.Value.AsSpan(5)), -1)) }).ToArray() },
            "lastLegal" => row with { Cells = board.Select((code, square) => new StateCell(CellName.Parse(square.ToString(System.Globalization.CultureInfo.InvariantCulture)), CellValue.Int(code))).ToArray() },
            "settleHold" => row with { Cells = [new(StateRow.SlotKey, CellValue.Int(60))] },
            "gameStarted" => row with { Cells = [new(StateRow.SlotKey, CellValue.Int(1))] },
            "turn" => row with { Cells = [new(StateRow.SlotKey, CellValue.Int(turn))] },
            "enPassantTarget" => row with { Cells = [new(StateRow.SlotKey, CellValue.Int(enPassant))] },
            _ => row,
        }).ToArray();
        world = world with { StateRaw = world.StateRaw with { World = rows } };
        Assert.True(StateArena.TryCreate(arena: out var arena, catalog: world.StateCatalog, options: null, reason: out var refusal, section: world.StateRaw, time: ArenaTime.Origin), refusal);
        Assert.True(arena.Catalog.TryResolve(StateLane.Document, "pieceCell", out var tokens));
        Assert.True(arena.TryWrite(rowOrdinal: tokens.Ordinal, key: arena.Catalog.Keys.Intern(CellName.Parse($"piece{mover}")), write: StateWriteKind.Set, operand: to, reason: out refusal), refusal);
        var judge = new RuleArenaSearchJudge(new ArenaSearchEffectHost(arena), WorldSearchCompilation.JudgeRules(WorldFactsCompiler.CompileAll(world)));
        judge.Judge(new ArenaSearchView(arena, 1, 1, ply));
        return arena;
    }

    private static long Read(StateArena arena, string row, string key = "$value") {
        Assert.True(arena.Catalog.TryResolve(StateLane.Document, row, out var handle));
        Assert.True(arena.TryRead(handle.Ordinal, arena.Catalog.Keys.Intern(CellName.Parse(key)), out var value));
        return value.Raw;
    }

    [Theory]
    [InlineData("castleRights", null, 15)]
    [InlineData("pieceCode", "piece12", 2)]
    public void DirectPositionEditsInvalidateAnOtherwiseMatchingAnswer(string row, string? key, long value) {
        var world = Chess();
        world = world with { Rules = [
            new WorldRule(Name: CellName.Parse("edit-position"),
                Gate: new ActionPredicate.CompareState(State: "$tick", Comparison: ActionStateComparison.Equal, Value: 500),
                Effects: [
                    new ActionEffect.SetState(State: "aiSide", Value: 0),
                    new ActionEffect.SetState(State: "aiBest", Key: "token", Value: 12),
                    new ActionEffect.SetState(State: "aiBest", Key: "to", Value: 63),
                    new ActionEffect.SetState(State: "aiBest", Key: "revision", Expression: ExpressionProgram.Parse("aiRevision")),
                    new ActionEffect.SetState(State: row, Key: key, Value: value),
                ]),
            .. world.Rules!,
        ] };
        using var fixture = Fixtures.FreshServer(world);
        for (var tick = 0; tick < 502; tick++) { fixture.Step(); }
        Assert.NotEqual(63, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "ai")!.Cells!.Single(cell => cell.Key.Value == "to").Value.Raw);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TheOpponentMovesPhysicalPiecesOnceAndThenWaits(int side) {
        var world = Chess();
        world = world with { StateRaw = world.StateRaw! with { World = world.StateRaw.World!.Select(row =>
            row.Name.Value is "aiSide" or "turn" ? row with { Cells = [new(StateRow.SlotKey, CellValue.Int(side))] } : row).ToArray() } };
        using var fixture = Fixtures.FreshServer(world);
        for (var tick = 0; tick < 900; tick++) { fixture.Step(); }
        long Slot(string name) => WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, name)!.Cells![0].Value.Raw;
        Assert.Equal(1 - side, Slot("turn"));
        Assert.Equal(1, Slot("verdict"));
        Assert.Equal(0, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "ai")!.Cells!.Single(cell => cell.Key.Value == "pending").Value.Raw);
        Assert.Equal(0, Slot("aiEnabled"));
        Assert.Equal(0, Slot("boardCollisions"));
        Assert.Equal(60, Slot("settleHold"));
    }

    [Theory]
    [InlineData(0, 7, 6, 5)]
    [InlineData(0, 0, 2, 3)]
    [InlineData(1, 31, 62, 61)]
    [InlineData(1, 24, 58, 59)]
    public void SearchCompletesBothCastlesForEitherSide(int side, int rook, int to, int rookTo) {
        var cells = new Dictionary<int, int> { [4] = 4, [28] = 60, [rook] = side * 56 + (rook % 8) };
        var arena = Judge(cells, side == 0 ? 4 : 28, to, turn: side);
        Assert.Equal(1, Read(arena, "verdict"));
        Assert.Equal(1 - side, Read(arena, "turn"));
        Assert.Equal(rookTo, Read(arena, "pieceCell", $"piece{rook}"));
    }

    [Theory]
    [InlineData(0, 36, 35, 43, 12, 19)]
    [InlineData(1, 28, 27, 19, 19, 12)]
    public void SearchRemovesTheEnPassantVictim(int side, int from, int victim, int to, int pawn, int captured) {
        var arena = Judge(new() { [4] = 4, [28] = 60, [pawn] = from, [captured] = victim }, pawn, to, turn: side, enPassant: to);
        Assert.Equal(1, Read(arena, "verdict"));
        Assert.Equal(-1, Read(arena, "pieceCell", $"piece{captured}"));
    }

    [Theory]
    [InlineData(0, 12, 52, 60, 5)]
    [InlineData(1, 19, 12, 4, -5)]
    public void SearchPromotesToAQueen(int side, int pawn, int from, int to, int queen) {
        var arena = Judge(new() { [4] = 0, [28] = 63, [pawn] = from }, pawn, to, turn: side);
        Assert.Equal(1, Read(arena, "verdict"));
        Assert.Equal(queen, Read(arena, "pieceCode", $"piece{pawn}"));
    }

    [Fact]
    public void EnPassantCannotExposeTheMovingSidesKing() {
        var arena = Judge(new() { [4] = 4, [28] = 56, [24] = 60, [12] = 36, [19] = 35 }, 12, 43, enPassant: 43);
        Assert.Equal(0, Read(arena, "verdict"));
        Assert.Equal(0, Read(arena, "turn"));
    }

    [Fact]
    public void LivePlayDoesNotCompleteAMissingCastlingRook() {
        var arena = Judge(new() { [4] = 4, [28] = 60, [7] = 7 }, 4, 6, ply: 0);
        Assert.Equal(0, Read(arena, "verdict"));
        Assert.Equal(7, Read(arena, "pieceCell", "piece7"));
    }
}
