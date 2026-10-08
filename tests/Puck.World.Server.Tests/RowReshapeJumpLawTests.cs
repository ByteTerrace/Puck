using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// A jump back across a row's re-declaration installs the document it jumps to. A row the live world re-declared from
/// keyed to slot comes back keyed, holding the document's cells, through every whole-document door: a history seek
/// back across the change (which reinstalls the keyframe's document through the load door), a <c>world.load</c> of
/// the earlier document, and an undo of the re-declaration. Each either installs the document or refuses by name with
/// the world untouched; none throws. Red leg: an install that carries the live arena's rows across by name refuses the
/// re-declared row mid-install and throws out of the door.
/// </summary>
public sealed class RowReshapeJumpLawTests {
    private const string Trace = "trace";

    private static WorldStateRow KeyedRow() => new(
        Name: CellName.Parse(candidate: Trace),
        Kind: CellKind.Int,
        Cells: [
            new StateCell(Key: CellName.Parse(candidate: "p1"), Value: CellValue.Int(value: 3L)),
            new StateCell(Key: CellName.Parse(candidate: "p2"), Value: CellValue.Int(value: 4L)),
        ]
    );
    private static WorldStateRow SlotRow() => new(
        Name: CellName.Parse(candidate: Trace),
        Kind: CellKind.Int,
        Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 9L))]
    );
    private static WorldDefinition KeyedDocument() => (Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(World: [KeyedRow()]),
    });
    private static WorldMutation Reshape() => new WorldMutation.UpsertStateRow(
        Principal: Principal.Console,
        Row: SlotRow()
    );
    private static WorldStateRow Row(WorldServer server) => server.Definition.State.Single(predicate: static row => (row.Name.Value == Trace));
    private static void AssertKeyed(WorldServer server) {
        var row = Row(server: server);

        Assert.True(condition: row.IsKeyed);
        Assert.Equal(
            expected: ["p1", "p2"],
            actual: row.Cells!.Select(selector: static cell => cell.Key.Value)
        );
    }

    [Fact]
    public void ASeekBackAcrossAKeyedToSlotReshapeReproducesTheKeyframe() {
        using var harness = new WorldHistoryHarness(definition: KeyedDocument());

        harness.Steps(count: 8);

        var keyframe = harness.History.KeyframeTicks[0];

        harness.Submit(mutation: Reshape());
        harness.Steps(count: 8);
        Assert.True(condition: Row(server: harness.Fixture.Server).IsSlot);

        // Through the load door, since a structural edit landed after the keyframe; the seek proves the hash.
        var report = harness.SeekAndProve(target: keyframe);

        Assert.True(condition: report.RebuiltDocument);
        AssertKeyed(server: harness.Fixture.Server);
        _ = harness.SeekAndProve(target: harness.History.HeadTick!.Value);
        Assert.True(condition: Row(server: harness.Fixture.Server).IsSlot);
    }
    [Fact]
    public void ALoadOfTheKeyedDocumentOverASlotRowInstallsItKeyed() {
        using var directory = new TemporaryDirectory(prefix: "puck-reshape-load-");
        var keyed = KeyedDocument();
        var bytes = WorldDefinitionSerialization.Serialize(definition: keyed);
        var path = Path.Combine(path1: directory.RootPath, path2: "keyed.world.json");

        File.WriteAllBytes(bytes: bytes, path: path);

        using var fixture = Fixtures.FreshServer(definition: keyed);

        fixture.Server.EnqueueMutation(mutation: Reshape());
        fixture.Step();
        Assert.True(condition: Row(server: fixture.Server).IsSlot);

        foreach (var kind in new[] { WorldRebuildKind.Load, WorldRebuildKind.Reload }) {
            fixture.Server.EnqueueRebuild(
                principal: Principal.Console,
                request: new WorldRebuildRequest(
                    ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: bytes),
                    Definition: keyed,
                    Force: true,
                    Kind: kind,
                    Origin: new WorldRebuildOrigin.File(Path: path)
                )
            );
            fixture.Step();
            AssertKeyed(server: fixture.Server);
            fixture.Server.EnqueueMutation(mutation: Reshape());
            fixture.Step();
            Assert.True(condition: Row(server: fixture.Server).IsSlot);
        }
    }
    [Fact]
    public void AnUndoOfAKeyedToSlotReshapeInstallsItKeyed() {
        using var fixture = Fixtures.FreshServer(definition: KeyedDocument());

        fixture.Server.EnqueueMutation(mutation: Reshape());
        fixture.Step();
        Assert.True(condition: Row(server: fixture.Server).IsSlot);
        fixture.Server.EnqueueUndo(
            count: 1,
            principal: Principal.Console
        );
        fixture.Step();
        AssertKeyed(server: fixture.Server);
    }
}
