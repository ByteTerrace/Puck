using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Laws for <c>world.undo</c> and <c>world.redo</c> acting on the acting principal's own edits: an undo takes
/// back only that principal's edit, a redo restores it in place, and either is refused by name, naming the row and the
/// other principal, when a later edit by someone else changed a row it would change.</summary>
public sealed class UndoRedoLawTests {
    private static readonly Principal Seat = Principal.Seat(slot: 0);

    private static WorldMutation Set(Principal principal, string row, long value) =>
        new WorldMutation.UpsertStateCell(principal, row, StateRow.SlotKey.Value, value, WorldDocumentWriteKind.Set);

    private static CellValue Read(WorldFixture fixture, string row) {
        Assert.True(WorldStateReadView.Of(reader: Principal.Console, server: fixture.Server).TryReadValue(row, StateRow.SlotKey.Value, out _, out var value));
        return value;
    }

    [Fact]
    public void ChangedRowsNamesTheKeyedRowAnEditReplaced() {
        var before = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [StateFixtures.IntSlot("a", 0), StateFixtures.IntSlot("b", 0)]),
        };
        var after = before.WithWorldState(rows: [before.StateRaw!.World![0], StateFixtures.IntSlot("b", 2)]);

        Assert.Equal(expected: ["$.state.world[name=\"b\"]"], actual: WorldDocumentWalker.ChangedRows(before: before, after: after));
        // An owned list held as an immutable list against one held as an immutable array still compares row by row.
        var relisted = before.WithWorldState(rows: System.Collections.Immutable.ImmutableList.CreateRange([before.StateRaw!.World![0], StateFixtures.IntSlot("b", 2)]));

        Assert.Equal(expected: ["$.state.world[name=\"b\"]"], actual: WorldDocumentWalker.ChangedRows(before: before, after: relisted));
    }

    [Fact]
    public void AnUndoTakesBackOnlyItsPrincipalsEditAndARedoRestoresIt() {
        using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [StateFixtures.IntSlot("a", 0), StateFixtures.IntSlot("b", 0)]),
        });
        var verdicts = new List<WorldEditEcho>();

        fixture.Server.EchoTap = echo => { if (echo.Kind == WorldEditEchoKind.Mutation) { verdicts.Add(echo); } };
        Assert.True(fixture.Server.GrantTable.TryGrant(new WorldGrant(Seat, WorldCapability.Operate, GrantSubject.All, false), Principal.Console, out var reason), reason);
        fixture.Server.EnqueueMutation(Set(Principal.Console, "a", 1));
        fixture.Step();
        fixture.Server.EnqueueMutation(Set(Seat, "b", 2));
        fixture.Step();

        // The console's undo takes back its own edit, not the seat's later one.
        fixture.Server.EnqueueUndo(count: 1, principal: Principal.Console);
        fixture.Step();
        Assert.False(verdicts[^1].Rejected, verdicts[^1].Message);
        Assert.Equal(CellValue.Int(value: 0), Read(fixture, "a"));
        Assert.Equal(CellValue.Int(value: 2), Read(fixture, "b"));

        fixture.Server.EnqueueUndo(count: 1, principal: Principal.Console, redo: true);
        fixture.Step();
        Assert.False(verdicts[^1].Rejected, verdicts[^1].Message);
        Assert.StartsWith("redid 1", verdicts[^1].Message, StringComparison.Ordinal);
        Assert.Equal(CellValue.Int(value: 1), Read(fixture, "a"));

        // The seat edits the row the console's edit changed; the console's undo is refused, naming the row and the seat.
        fixture.Server.EnqueueMutation(Set(Seat, "a", 3));
        fixture.Step();
        fixture.Server.EnqueueUndo(count: 1, principal: Principal.Console);
        fixture.Step();
        Assert.True(verdicts[^1].Rejected);
        Assert.Contains("$.state.world[name=\"a\"]", verdicts[^1].Message, StringComparison.Ordinal);
        Assert.Contains("seat1", verdicts[^1].Message, StringComparison.Ordinal);
        Assert.Equal(CellValue.Int(value: 3), Read(fixture, "a"));

        // The seat's own undo is its to make; its next edit leaves it nothing to redo.
        fixture.Server.EnqueueUndo(count: 1, principal: Seat);
        fixture.Step();
        Assert.False(verdicts[^1].Rejected, verdicts[^1].Message);
        Assert.Equal(CellValue.Int(value: 1), Read(fixture, "a"));
        fixture.Server.EnqueueMutation(Set(Seat, "b", 4));
        fixture.Step();
        fixture.Server.EnqueueUndo(count: 1, principal: Seat, redo: true);
        fixture.Step();
        Assert.True(verdicts[^1].Rejected);
        Assert.StartsWith("redo refused: nothing to redo", verdicts[^1].Message, StringComparison.Ordinal);
    }
}
