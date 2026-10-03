using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the document-and-arena unit prepared for a declaration-changing mutation.</summary>
public sealed class WorldArenaReplacementLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ACapacityShrinkRefusesBeforePreparingAnArena(bool administrative) {
        var document = Document();

        document = document with {
            PopulationRaw = document.Population with {
                CapacityRaw = 128,
            },
        };
        var candidate = document with { PopulationRaw = document.Population with { CapacityRaw = 32 } };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(compilation: out _, definition: candidate, reason: out var reason), userMessage: reason);
        using var fixture = Fixtures.FreshServer(definition: document);
        var first = fixture.JoinSeat(slot: 0);
        var second = fixture.JoinSeat(slot: 1);
        var arena = fixture.Server.Arena;
        var before = fixture.DefinitionBytes();
        var keyCount = arena.Keys.Count;
        var hash = WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0UL);
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };
        fixture.Server.EnqueueRebuild(
            principal: Principal.Console,
            request: new WorldRebuildRequest(
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: candidate)),
                Definition: candidate,
                Force: true,
                Kind: WorldRebuildKind.Load,
                Origin: new WorldRebuildOrigin.File(Path: "capacity.world.json")
            )
        );
        var failure = Xunit.Record.Exception(testCode: () => {
            if (administrative) { fixture.Server.DrainAdministrative(); } else { fixture.Step(); }
        });

        Assert.Null(@object: failure);
        Assert.Contains(collection: refusals, filter: static refusal => refusal.Contains(
            comparisonType: StringComparison.Ordinal,
            value: "population capacity 32 differs from the boot-allocated capacity 128"
        ));
        Assert.Same(expected: arena, actual: fixture.Server.Arena);
        Assert.Equal(expected: before, actual: fixture.DefinitionBytes());
        Assert.Equal(expected: keyCount, actual: arena.Keys.Count);
        Assert.Same(expected: first, actual: fixture.Server.Body(index: 0));
        Assert.Same(expected: second, actual: fixture.Server.Body(index: 1));
        Assert.Equal(expected: 128, actual: fixture.Server.Population.Capacity);
        if (administrative) {
            Assert.Equal(expected: hash, actual: WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0UL));
        }
    }
    [Fact]
    public void AScalarMintRefusedByTheLiveKeyLedgerLeavesDefinitionAndArenaAlone() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var arena = fixture.Server.Arena;

        FillKeyLedger(arena: arena);
        var before = fixture.DefinitionBytes();
        var refusals = new List<string>();

        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusals.Add(item: echo.Message); } };
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console,
            Row: "bag",
            Key: "fresh-member",
            Value: 1L,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        Assert.Same(expected: arena, actual: fixture.Server.Arena);
        Assert.Equal(expected: before, actual: fixture.DefinitionBytes());
        Assert.Contains(
            collection: refusals,
            filter: static refusal => refusal.Contains(
                comparisonType: StringComparison.Ordinal,
                value: $"already holds {StateCapacity.MaxCellKeys} distinct keys"
            )
        );
        Assert.False(condition: arena.Keys.TryResolve(
            name: Name(value: "fresh-member"),
            key: out _
        ));
    }
    [Fact]
    public void ARefusedReplacementLeavesTheInstalledDefinitionArenaAndLedgerAlone() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var arena = fixture.Server.Arena;

        FillKeyLedger(arena: arena);
        var before = fixture.DefinitionBytes();
        var keyCount = arena.Keys.Count;
        var keyBytes = arena.Keys.Bytes;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: new WorldStateRow(
                Name: Name(value: "added"),
                Kind: CellKind.Int,
                Capacity: 1,
                Domain: new StateDomain.Keys(),
                Cells: [new StateCell(
                        Key: Name(value: "replacement-new-key"),
                        Value: CellValue.Int(value: 1L)
                    )]
            )
        ));
        fixture.Step();

        Assert.Same(expected: arena, actual: fixture.Server.Arena);
        Assert.Equal(expected: before, actual: fixture.DefinitionBytes());
        Assert.Equal(expected: keyCount, actual: fixture.Server.Arena.Keys.Count);
        Assert.Equal(expected: keyBytes, actual: fixture.Server.Arena.Keys.Bytes);
        Assert.False(condition: fixture.Server.Arena.Keys.TryResolve(
            name: Name(value: "replacement-new-key"),
            key: out _
        ));
    }
    [Fact]
    public void APreparedReplacementCarriesTheCommittedOrphanLedger() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var before = fixture.Server.Arena;
        var orphan = MintOrphan(arena: before);
        var keyCount = before.Keys.Count;
        var keyBytes = before.Keys.Bytes;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: new WorldStateRow(
                Name: Name(value: "added"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 7L)
                    )]
            )
        ));
        fixture.Step();

        Assert.NotSame(expected: before, actual: fixture.Server.Arena);
        Assert.True(condition: fixture.Server.Arena.Keys.TryResolve(key: out _, name: orphan));
        Assert.Equal(expected: keyCount, actual: fixture.Server.Arena.Keys.Count);
        Assert.Equal(expected: keyBytes, actual: fixture.Server.Arena.Keys.Bytes);
        Assert.NotNull(@object: WorldDefinitionRows.FindStateRow(
            rows: fixture.Server.Definition.State,
            name: "added"
        ));
    }

    private static CellName MintOrphan(StateArena arena) {
        var name = Name(value: "committed-orphan");

        Assert.True(condition: arena.Catalog.TryResolve(
            handle: out var row,
            lane: StateLane.Document,
            name: Name(value: "bag")
        ));
        Assert.True(condition: arena.TryMint(
            key: out var key,
            name: name,
            reason: out var reason,
            rowOrdinal: row.Ordinal,
            value: CellValue.Int(value: 1L)
        ), userMessage: reason);
        Assert.True(condition: arena.TryRemove(
            key: key,
            reason: out reason,
            rowOrdinal: row.Ordinal
        ), userMessage: reason);

        return name;
    }
    private static void FillKeyLedger(StateArena arena) {
        Assert.True(condition: arena.Catalog.TryResolve(
            handle: out var row,
            lane: StateLane.Document,
            name: Name(value: "bag")
        ));

        for (var index = arena.Keys.Count; (index < StateCapacity.MaxCellKeys); index++) {
            Assert.True(condition: arena.TryMint(
                key: out var key,
                name: Name(value: $"orphan-{index}"),
                reason: out var reason,
                rowOrdinal: row.Ordinal,
                value: CellValue.Int(value: index)
            ), userMessage: reason);
            Assert.True(condition: arena.TryRemove(
                key: key,
                reason: out reason,
                rowOrdinal: row.Ordinal
            ), userMessage: reason);
        }
    }
    private static WorldDefinition Document() => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: Name(value: "bag"),
                Kind: CellKind.Int,
                Capacity: 2,
                Domain: new StateDomain.Keys()
            ), new WorldStateRow(
                Name: Name(value: "seed"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 0L)
                    )]
            )]),
    };
    private static CellName Name(string value) => CellName.Parse(candidate: value);
}
