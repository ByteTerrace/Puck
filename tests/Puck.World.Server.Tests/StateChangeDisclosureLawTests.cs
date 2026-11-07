using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class StateChangeDisclosureLawTests(ITestOutputHelper output) {
    private static WorldDefinition Document(int vectors = 1) => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(
            Spaces: [new StateSpace(dimensions: 8, model: "test-model", name: CellName.Parse("lore"), revision: "1")],
            World: [new WorldStateRow(Name: CellName.Parse("hand"), Kind: CellKind.Vector, Space: "lore",
                Visibility: new StateVisibility(Hidden: HiddenCells.Omit),
                Cells: Enumerable.Range(0, vectors).Select(index => new StateCell(CellName.Parse("pub" + index),
                    CellValue.Vector(new sbyte[] { 127, 0, 0, 0, 0, 0, 0, 0 })))
                    .Append(new StateCell(CellName.Parse("secret"), CellValue.Vector(new sbyte[] { 127, 0, 0, 0, 0, 0, 0, 0 }),
                        Visibility: new StateVisibility(Readers: ["seat1"]))).ToArray())])
    };
    private static WorldRecipientView View(WorldServer server, WorldDisclosureImages images, Principal? recipient = null) =>
        new(() => server.Definition, () => server.Arena, recipient ?? Principal.Seat(1), WorldDisclosureTier.Replica,
            () => true, images, server.AuthorityIdentity, () => server.DeliveryTime);

    [Fact]
    public void DefinitionSeedsDeltasAndAReadBetweenDeliveriesCannotConsumeAnInPlaceVectorWrite() {
        using var fixture = Fixtures.FreshServer(Document());
        var images = new WorldDisclosureImages();
        var view = View(fixture.Server, images);
        var first = new WorldDeliveryCursor();
        var second = new WorldDeliveryCursor();
        var arena = fixture.Server.Arena;
        Assert.True(arena.Catalog.TryResolve(StateLane.Document, "hand", out var row));
        Assert.True(arena.Keys.TryResolve(CellName.Parse("secret"), out var secret));
        Assert.True(arena.Keys.TryResolve(CellName.Parse("pub0"), out var pub));
        Assert.NotNull(first.DeliverDefinition(view));
        Assert.NotNull(second.DeliverDefinition(view));
        var before = view.Image(row.Ordinal);
        Assert.True(arena.TryWriteVector(row.Ordinal, secret, [0, 127, 0, 0, 0, 0, 0, 0], out var reason), reason);
        _ = view.Definition;
        Assert.Empty(view.Advance(first, [row.Ordinal]));
        Assert.Empty(view.Advance(second, [row.Ordinal]));
        Assert.True(arena.TryWriteVector(row.Ordinal, pub, [0, 127, 0, 0, 0, 0, 0, 0], out reason), reason);
        _ = view.Observations();
        var delivered = Assert.Single(view.Advance(first, [row.Ordinal]));
        Assert.Single(view.Advance(second, [row.Ordinal]));
        Assert.Equal((sbyte)127, before.Cells[0].Value.AsVector.Span[0]);
        Assert.Equal((sbyte)0, delivered.Image.Cells[0].Value.AsVector.Span[0]);
        Assert.Empty(view.Advance(first, [row.Ordinal]));
        first.Release();
        Assert.NotNull(first.DeliverDefinition(view));
        Assert.Empty(view.Advance(first, [row.Ordinal]));
    }

    [Fact]
    public void AReplicaPrimesCountMetadataThenKeepsItLiveWithoutResendingHiddenValues() {
        var document = Document();
        document = document.WithWorldState([document.AuthoredState[0] with { Visibility = new StateVisibility(Hidden: HiddenCells.Count) }]);
        using var fixture = Fixtures.FreshServer(document);
        var images = new WorldDisclosureImages();
        var view = View(fixture.Server, images);
        var cursor = new WorldDeliveryCursor();
        Assert.NotNull(cursor.DeliverDefinition(view));
        Assert.Equal(1, Assert.Single(view.Advance(cursor, [])).Image.Observation!.HiddenCount);
        Assert.Empty(view.Advance(cursor, []));
        var arena = fixture.Server.Arena;
        Assert.True(arena.Catalog.TryResolve(StateLane.Document, "hand", out var row));
        Assert.True(arena.Keys.TryResolve(CellName.Parse("secret"), out var secret));
        Assert.True(arena.TryWriteVector(row.Ordinal, secret, [0, 127, 0, 0, 0, 0, 0, 0], out var reason), reason);
        Assert.Empty(view.Advance(cursor, [row.Ordinal]));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(Principal.Console, document.AuthoredState[0] with {
            Cells = [document.AuthoredState[0].Cells![0]]
        }));
        fixture.Step();
        var replacement = Assert.Single(view.Advance(cursor, [row.Ordinal]));
        Assert.Equal(0, replacement.Image.Observation!.HiddenCount);
    }

    [Fact]
    public void SpectatorsShareOneCompositionPerAudienceAndChangedRow() {
        var spectators = 32;
        var vectors = 64;
        using var fixture = Fixtures.FreshServer(Document(vectors));
        var images = new WorldDisclosureImages();
        var views = Enumerable.Range(0, spectators).Select(index => View(fixture.Server, images, Principal.Session(index, 1))).ToArray();
        var cursors = views.Select(_ => new WorldDeliveryCursor()).ToArray();
        var arena = fixture.Server.Arena;
        Assert.True(arena.Catalog.TryResolve(StateLane.Document, "hand", out var row));
        Assert.True(arena.Keys.TryResolve(CellName.Parse("secret"), out var secret));
        Assert.True(arena.Keys.TryResolve(CellName.Parse("pub0"), out var pub));
        for (var index = 0; index < spectators; index++) { cursors[index].DeliverDefinition(views[index]); }
        arena.CopyChangedRows(new bool[arena.Rows.Count], clear: true);
        images.BeginTick(arena.ChangedKeys);
        Assert.True(arena.TryWriteVector(row.Ordinal, secret, [0, 127, 0, 0, 0, 0, 0, 0], out var reason), reason);
        for (var index = 0; index < spectators; index++) { Assert.Empty(views[index].Advance(cursors[index], [row.Ordinal])); }
        Assert.Equal(0, images.Compositions);
        output.WriteLine($"{spectators} spectators x {vectors} vectors, hidden-only: {images.Describe()}");
        Assert.True(arena.TryWriteVector(row.Ordinal, pub, [0, 127, 0, 0, 0, 0, 0, 0], out reason), reason);
        for (var index = 0; index < spectators; index++) { Assert.Single(views[index].Advance(cursors[index], [row.Ordinal])); }
        Assert.Equal(1, images.Compositions);
        output.WriteLine($"{spectators} spectators x {vectors} vectors, visible write: {images.Describe()}");
    }
}
