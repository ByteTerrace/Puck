using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSocialImportReservationLawTests {
    [Fact]
    public void PreparedImportOwnsItsInputAndCommitsExactlyOnceWithoutAllocating() {
        var source = Bank(); source.Observe(Evidence(1)); var incoming = Import(source, 1);
        var bank = Bank(); bank.TryReserveImport(Transfer(), [new(Person(1), 1, 1)], out _);
        var before = bank.StateHash;
        Assert.True(bank.TryPrepareReservedImport(Transfer(), [incoming], out var token, out var reason), reason);
        Assert.Equal(before, bank.StateHash); Assert.Equal(0, bank.ImpressionCount);
        ((WorldSocialImpressionCheckpoint[])incoming.Memory.Impressions)[0] = default;
        // Warm the actual commit method on another bank before measuring this independent prepared transaction.
        var warm = Bank(); warm.TryReserveImport(Transfer(), [new(Person(1), 1, 1)], out _);
        Assert.True(warm.TryImportReserved(Transfer(), [Import(source, 1)], out _));
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var committed = bank.TryCommitReservedImport(token!, out reason);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Assert.True(committed, reason); Assert.Equal(0L, allocated);
        Assert.True(Read(bank, 1).Known); Assert.Equal(0, bank.ReservedObserverCount);
        var after = bank.StateHash;
        Assert.False(bank.TryCommitReservedImport(token!, out _)); Assert.Equal(after, bank.StateHash);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void StalePreparedTokensCannotConsumeCurrentReservations(int change) {
        var source = Bank(); source.Observe(Evidence(1)); var bank = Bank();
        bank.TryReserveImport(Transfer(), [new(Person(1), 1, 1)], out _);
        Assert.True(bank.TryPrepareReservedImport(Transfer(), [Import(source, 1)], out var token, out _));
        if (change == 0) { bank.Advance(1); }
        else if (change == 1) { bank.Observe(Evidence(2)); }
        else if (change == 2) { bank.CancelImportReservation(Transfer()); bank.TryReserveImport(Transfer(), [new(Person(1), 1, 1)], out _); }
        else { bank = WorldSocialMemory.Restore(bank.Policy, bank.Capture()); }
        var before = bank.StateHash;
        Assert.False(bank.TryCommitReservedImport(token!, out _)); Assert.Equal(before, bank.StateHash);
        Assert.Equal(1, bank.ReservedObserverCount);
        Assert.True(bank.TryImportReserved(Transfer(), [Import(source, 1)], out var reason), reason);
        Assert.True(Read(bank, 1).Known);
    }
}
