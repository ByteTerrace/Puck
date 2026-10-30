using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Recovery of the key ledger is a fold over the crossing log: each record sets the fate of the key it names,
/// so the ledger a recovery leaves depends only on the records it redid, not on how they were split across calls. A
/// silo redoes its journal one record at a time; a restarted row redoes its whole log at once.</summary>
public sealed partial class TransferKeyOnceLawTests {
    private const string Source = "machine-a/boot";

    // Every table the key ledger keeps, in a canonical order: settled keys with their fate and durability, retained
    // commits, live leases, floors, and the row count both bounds read.
    private static string Ledger(WorldServer server) => server.ExecuteAuthorityOperation(operation: () => {
        var captured = server.TransferEscrow.Capture();

        return string.Join(
            separator: " | ",
            values: [
                string.Join(separator: ",", values: captured.SpentKeys.OrderBy(keySelector: static row => row.Key.TransferId).Select(selector: static row => $"{row.Key.TransferId}:{(row.Committed ? "committed" : "dead")}:{(row.Durable ? "durable" : "held")}")),
                string.Join(separator: ",", values: captured.Committed.Select(selector: static row => row.Key.TransferId).Order()),
                string.Join(separator: ",", values: captured.Leases.Select(selector: static row => row.Key.TransferId).Order()),
                string.Join(separator: ",", values: captured.Floors.OrderBy(keySelector: static row => row.SourceAuthority, comparer: StringComparer.Ordinal).Select(selector: static row => $"{row.SourceAuthority}:{row.Floor}")),
                $"rows={server.TransferEscrow.Counts.TransferKeys}",
            ]
        );
    });
    private static void ReserveAndCommit(WorldServer destination, ulong transferId, ulong floor, int traveller) {
        var reserved = destination.ReserveTransfer(request: Reservation(floor: floor, transferId: transferId, traveller: traveller));

        Assert.True(condition: reserved.Accepted, userMessage: reserved.Reason);
        Assert.Equal(
            expected: WorldTransferStatus.Committed,
            actual: destination.CommitTransfer(members: [Member], reason: out _, sourceAuthority: Source, transferId: transferId)
        );
    }
    // Restarts the destination from its durable image with nothing redone, then redoes `calls` in order, each one call.
    private static string Recover(CrossingWorld world, IEnumerable<IReadOnlyList<WorldCrossingEntry>> calls) {
        using var restarted = world.Restart(
            destinationDied: true,
            destinationLogLost: true,
            sourceDied: false
        );

        foreach (var call in calls) {
            restarted.Host.RecoverCrossings(
                entries: call,
                row: restarted.Destination.Instance
            );
        }
        return Ledger(server: restarted.Destination.Server);
    }

    // THE LAW (recovery is a fold): one crossing log holding two arrivals across a rising floor, a tombstone, a lease
    // left dead by an abort and a lease still live at the crash, recovered from a checkpoint before all of it, leaves the
    // same ledger however the redo is cut: the whole log at once, split at every position, or one record per call as a
    // silo redoes it. Red before the fold: a cut after a lease declared its key dead, the arrival that followed landed it
    // without clearing that death, and the next floor dropped the committed key's row.
    [Fact]
    public void RecoveringTheLogLeavesOneLedgerHoweverTheRedoIsCut() {
        using var world = CrossingWorld.Build();
        var destination = world.Destination.Server;

        ReserveAndCommit(destination: destination, floor: 0, transferId: 1, traveller: 1);
        ReserveAndCommit(destination: destination, floor: 2, transferId: 2, traveller: 2);
        Assert.True(condition: destination.ReserveTransfer(request: Reservation(floor: 2, transferId: 3, traveller: 3)).Accepted);
        destination.AbortTransfer(sourceAuthority: Source, transferId: 3);
        Assert.Equal(expected: WorldTransferStatus.Missing, actual: destination.TransferStatus(sourceAuthority: Source, transferId: 3));
        Assert.True(condition: destination.ReserveTransfer(request: Reservation(floor: 4, transferId: 4, traveller: 4)).Accepted);
        destination.AbortTransfer(sourceAuthority: Source, transferId: 4);
        Assert.True(condition: destination.ReserveTransfer(request: Reservation(floor: 4, transferId: 5, traveller: 5)).Accepted);

        var log = world.DestinationLog.Read();

        Assert.Equal(
            expected: [typeof(WorldCrossingRecord.Lease), typeof(WorldCrossingRecord.Arrival), typeof(WorldCrossingRecord.Lease), typeof(WorldCrossingRecord.Arrival), typeof(WorldCrossingRecord.Lease), typeof(WorldCrossingRecord.Tombstone), typeof(WorldCrossingRecord.Lease), typeof(WorldCrossingRecord.Lease)],
            actual: log.Select(selector: static entry => entry.Record.GetType())
        );

        var whole = Recover(calls: [log], world: world);

        // Both arrivals committed below the floor keep their rows; the dead keys below it are gone; the lease the abort
        // left and the lease live at the crash are dead.
        Assert.Equal(expected: $"4:dead:durable,5:dead:durable | 1,2 |  | {Source}:4 | rows=5", actual: whole);
        Assert.Equal(expected: whole, actual: Recover(calls: log.Select(selector: static entry => (IReadOnlyList<WorldCrossingEntry>)[entry]), world: world));
        for (var cut = 0; (cut <= log.Count); cut++) {
            Assert.Equal(expected: whole, actual: Recover(calls: [[.. log.Take(count: cut)], [.. log.Skip(count: cut)]], world: world));
        }
    }

    // THE LAW (per-record recovery keeps its accounting): a silo redoes two arrivals across a rising floor one record at
    // a time; a delayed acknowledgement of the first and a reservation raising the floor past both then leave the
    // ledger's count matching its rows. Red before the fold: the first committed key's row was dropped by the floor, the
    // acknowledgement dropped it again, and the next floor threw while pruning.
    [Fact]
    public void ARecordByRecordRecoveryKeepsItsRowsThroughALaterAcknowledgementAndFloor() {
        using var world = CrossingWorld.Build();

        ReserveAndCommit(destination: world.Destination.Server, floor: 0, transferId: 1, traveller: 1);
        ReserveAndCommit(destination: world.Destination.Server, floor: 2, transferId: 2, traveller: 2);

        var log = world.DestinationLog.Read();

        using var restarted = world.Restart(
            destinationDied: true,
            destinationLogLost: true,
            sourceDied: false
        );
        var destination = restarted.Destination.Server;

        foreach (var entry in log) {
            restarted.Host.RecoverCrossings(entries: [entry], row: restarted.Destination.Instance);
        }
        Assert.Equal(expected: $" | 1,2 |  | {Source}:2 | rows=3", actual: Ledger(server: destination));

        destination.AcknowledgeTransfer(sourceAuthority: Source, transferId: 1);
        Assert.Equal(expected: $" | 2 |  | {Source}:2 | rows=2", actual: Ledger(server: destination));
        var next = destination.ReserveTransfer(request: Reservation(floor: 3, transferId: 3, traveller: 3));

        Assert.True(condition: next.Accepted, userMessage: next.Reason);
        Assert.Equal(expected: $" | 2 | 3 | {Source}:3 | rows=3", actual: Ledger(server: destination));
    }

    // THE LAW (a recovered floor prunes recovered deaths): a source's reservations, each aborted and each carrying its
    // own id as its settled floor, leave a floor and one dead row live; recovering every lease record together from a
    // checkpoint before them leaves the same two rows. Red before the fold: the recovered leases were held aside where no
    // floor could reach them, and every one stayed a row.
    [Fact]
    public void ABatchRecoveryOfManyAbortedLeasesKeepsOnlyTheRowsAboveTheFloor() {
        using var world = CrossingWorld.Build();
        var destination = world.Destination.Server;

        for (var id = 1UL; (id <= 100UL); id++) {
            Assert.True(condition: destination.ReserveTransfer(request: Reservation(floor: id, transferId: id)).Accepted);
            destination.AbortTransfer(sourceAuthority: Source, transferId: id);
        }
        Assert.Equal(expected: 2, actual: LedgerRows(destination: destination));

        using var restarted = world.Restart(
            destinationDied: true,
            sourceDied: false
        );

        Assert.Equal(expected: $"100:dead:durable |  |  | {Source}:100 | rows=2", actual: Ledger(server: restarted.Destination.Server));
    }
}
