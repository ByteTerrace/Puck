using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>games/mancala.world.json</c> is a self-contained, placement-addressed, reusable Mancala (Kalah)
/// module. Sowing is closed-form via cyclic distance arithmetic over 13 active pits, skipping the opponent's store.
/// Landing the last seed in own store grants an extra turn. Landing in an empty own pit sweeps that seed and the
/// opposite pit into own store. When either side empties, remaining seeds are swept into the respective store and
/// the winner is determined.
/// </summary>
public sealed class MancalaModuleLawTests {
    private static string RepoRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    private static WorldDefinition LoadGarden() {
        var path = Path.Combine(RepoRoot(), "src", "Puck.World", "Assets", "worlds", "puck.world.json");

        Assert.True(WorldDefinitionLoader.TryLoadFile(path, out var definition, out var reason), reason);

        return definition!;
    }

    private static long ReadCell(WorldFixture fixture, string row, string key) {
        Assert.True(condition: WorldStateReader.TryRead(
            definition: fixture.Server.Definition,
            rowName: row,
            key: key,
            tick: fixture.Server.NextInputTick,
            row: out _,
            rawValue: out var raw,
            text: out _
        ));

        return Assert.IsType<long>(@object: raw);
    }

    private static long ReadSlot(WorldFixture fixture, string row) => ReadCell(fixture, row, WorldStateRow.SlotKey);

    private static long[] ReadBoard(WorldFixture fixture) {
        var board = new long[14];

        for (var i = 0; i < 14; i++) {
            board[i] = ReadCell(fixture, "mancalaBoard", i.ToString());
        }

        return board;
    }

    private static void PlayPit(WorldFixture fixture, LoopbackTransport transport, int pit) {
        transport.SubmitWorldMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "mancalaSelectedPit",
            Key: WorldStateRow.SlotKey,
            Value: pit,
            Kind: WorldDocumentWriteKind.Set
        ));
        transport.SubmitWorldMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "mancalaMoveRequest",
            Key: WorldStateRow.SlotKey,
            Value: 1L,
            Kind: WorldDocumentWriteKind.Add
        ));
        fixture.Step();
    }

    private static void SetPits(WorldFixture fixture, LoopbackTransport transport, params (int Pit, long Seeds)[] pits) {
        foreach (var (pit, seeds) in pits) {
            transport.SubmitWorldMutation(mutation: new WorldMutation.UpsertStateCell(
                Principal: WorldPrincipal.Console,
                Row: "mancalaBoard",
                Key: pit.ToString(),
                Value: seeds,
                Kind: WorldDocumentWriteKind.Set
            ));
        }

        fixture.Step();
    }

    private static void SetTurn(WorldFixture fixture, LoopbackTransport transport, int turn) {
        transport.SubmitWorldMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "mancalaTurn",
            Key: WorldStateRow.SlotKey,
            Value: turn,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();
    }

    [Fact]
    public void InitialBoard_Has48Seeds_AndZeroStores() {
        using var fixture = Fixtures.FreshServer(definition: LoadGarden());

        var board = ReadBoard(fixture);

        // Pits 0..5 South: 4 each
        for (var i = 0; i < 6; i++) {
            Assert.Equal(4L, board[i]);
        }

        // South Store 6: 0
        Assert.Equal(0L, board[6]);

        // Pits 7..12 North: 4 each
        for (var i = 7; i < 13; i++) {
            Assert.Equal(4L, board[i]);
        }

        // North Store 13: 0
        Assert.Equal(0L, board[13]);

        Assert.Equal(0L, ReadSlot(fixture, "mancalaTurn"));
        Assert.Equal(0L, ReadSlot(fixture, "mancalaStatus"));
    }

    [Fact]
    public void StandardSow_And_ExtraTurn_WhenLandingInOwnStore() {
        using var fixture = Fixtures.FreshServer(definition: LoadGarden());
        var transport = new LoopbackTransport(server: fixture.Server);

        // Player 0 plays Pit 2 (4 seeds). Sows into 3, 4, 5, 6 (Store 0).
        PlayPit(fixture, transport, 2);

        var board = ReadBoard(fixture);

        Assert.Equal(0L, board[2]); // Selected pit emptied
        Assert.Equal(5L, board[3]); // Pit 3 got +1
        Assert.Equal(5L, board[4]); // Pit 4 got +1
        Assert.Equal(5L, board[5]); // Pit 5 got +1
        Assert.Equal(1L, board[6]); // Store 0 got +1 (landing cell!)

        // Last seed landed in Store 6 -> extra turn!
        Assert.Equal(0L, ReadSlot(fixture, "mancalaTurn"));
    }

    [Fact]
    public void NormalTurnTransfer_WhenLandingInRegularPit() {
        using var fixture = Fixtures.FreshServer(definition: LoadGarden());
        var transport = new LoopbackTransport(server: fixture.Server);

        // Player 0 plays Pit 0 (4 seeds). Sows into 1, 2, 3, 4.
        PlayPit(fixture, transport, 0);

        var board = ReadBoard(fixture);

        Assert.Equal(0L, board[0]);
        Assert.Equal(5L, board[1]);
        Assert.Equal(5L, board[2]);
        Assert.Equal(5L, board[3]);
        Assert.Equal(5L, board[4]);
        Assert.Equal(4L, board[5]);
        Assert.Equal(0L, board[6]);

        // Last seed landed in Pit 4 (not store) -> turn transfers to Player 1!
        Assert.Equal(1L, ReadSlot(fixture, "mancalaTurn"));
    }

    [Fact]
    public void OpponentStore_IsSkipped_ByBothPlayers() {
        using var fixture = Fixtures.FreshServer(definition: LoadGarden());
        var transport = new LoopbackTransport(server: fixture.Server);

        // Set Pit 5 to have 8 seeds. For P0: sows into 6, 7, 8, 9, 10, 11, 12, then skips 13, lands in 0.
        SetPits(fixture, transport, (5, 8L));

        PlayPit(fixture, transport, 5);

        var board = ReadBoard(fixture);

        Assert.Equal(0L, board[5]);
        Assert.Equal(1L, board[6]); // P0 Store got 1
        Assert.Equal(5L, board[7]);
        Assert.Equal(5L, board[8]);
        Assert.Equal(5L, board[9]);
        Assert.Equal(5L, board[10]);
        Assert.Equal(5L, board[11]);
        Assert.Equal(5L, board[12]);
        Assert.Equal(0L, board[13]); // Opponent Store 13 MUST be skipped (remains 0)!
        Assert.Equal(5L, board[0]); // 8th seed wrapped around store 13 into pit 0!
    }

    [Fact]
    public void Capture_Rule_SweepsLandingSeedAndOppositePit() {
        using var fixture = Fixtures.FreshServer(definition: LoadGarden());
        var transport = new LoopbackTransport(server: fixture.Server);

        // Setup: P0 Pit 1 is empty (0 seeds). Opposite pit 11 has 5 seeds.
        // P0 Pit 0 has 1 seed. Sowing pit 0 lands in pit 1 (empty own pit)!
        SetPits(fixture, transport, (0, 1L), (1, 0L), (11, 5L));

        PlayPit(fixture, transport, 0);

        var board = ReadBoard(fixture);

        Assert.Equal(0L, board[0]);
        Assert.Equal(0L, board[1]); // Landing pit was captured -> 0!
        Assert.Equal(0L, board[11]); // Opposite pit was captured -> 0!
        Assert.Equal(6L, board[6]); // P0 Store got landing (1) + opposite (5) = 6 seeds!
    }

    [Fact]
    public void LargeWrapAround_SowsMoreThan13Seeds() {
        using var fixture = Fixtures.FreshServer(definition: LoadGarden());
        var transport = new LoopbackTransport(server: fixture.Server);

        // Setup: P0 Pit 5 has 15 seeds.
        // 13-pit cycle receives 1 seed everywhere, first 2 pits (6 and 7) receive 2 seeds.
        // Pit 5 loses 15, receives 1 back -> ends at 1! Store 13 skipped -> remains 0!
        SetPits(fixture, transport, (5, 15L));

        PlayPit(fixture, transport, 5);

        var board = ReadBoard(fixture);

        Assert.Equal(1L, board[5]); // 15 seeds sown: 1 full lap + 2 extra, pit 5 gets 1 back!
        Assert.Equal(2L, board[6]); // Store 6 (dist 1) gets 2
        Assert.Equal(6L, board[7]); // Pit 7 (dist 2) had 4 + 2 = 6
        Assert.Equal(0L, board[13]); // Opponent store 13 skipped -> remains 0
    }

    [Fact]
    public void EndGame_SweepsRemainingSeeds_AndDeclaresWinner() {
        using var fixture = Fixtures.FreshServer(definition: LoadGarden());
        var transport = new LoopbackTransport(server: fixture.Server);

        // Setup: P0 has only 1 seed in Pit 5, all other P0 pits (0..4) are 0.
        // P1 has 2 seeds in Pit 7, 0 in pits 8..12.
        // Stores: P0 Store 6 has 25 seeds, P1 Store 13 has 20 seeds.
        SetPits(fixture, transport,
            (0, 0L), (1, 0L), (2, 0L), (3, 0L), (4, 0L), (5, 1L), (6, 25L),
            (7, 2L), (8, 0L), (9, 0L), (10, 0L), (11, 0L), (12, 0L), (13, 20L));

        // P0 plays pit 5 (1 seed). Sows into store 6.
        // Now P0's pits are ALL EMPTY!
        // End game triggers: P1's remaining 2 seeds in pit 7 are swept into Store 13 (20 + 2 = 22).
        // P0 store ends at 25 + 1 = 26.
        // P0 wins (26 > 22) -> status 1!
        PlayPit(fixture, transport, 5);

        var board = ReadBoard(fixture);

        for (var i = 0; i < 6; i++) {
            Assert.Equal(0L, board[i]);
        }

        for (var i = 7; i < 13; i++) {
            Assert.Equal(0L, board[i]);
        }

        Assert.Equal(26L, board[6]);
        Assert.Equal(22L, board[13]);
        Assert.Equal(1L, ReadSlot(fixture, "mancalaStatus")); // 1: P0 won!
    }
}
