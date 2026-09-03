using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSocialImportReservationLawTests {
    private static void SameSnapshot(WorldSocialMemoryCheckpoint expected, WorldSocialMemoryCheckpoint actual) {
        Assert.Equal(expected.PolicyIdentity, actual.PolicyIdentity);
        Assert.Equal(expected.EngineTick, actual.EngineTick);
        Assert.Equal(expected.EvidenceAttempts, actual.EvidenceAttempts);
        Assert.Equal(expected.ReclaimedReceipts, actual.ReclaimedReceipts);
        Assert.Equal(expected.NextOrdinal, actual.NextOrdinal);
        Assert.Equal(expected.Impressions, actual.Impressions);
        Assert.Equal(expected.Receipts, actual.Receipts);
        Assert.Empty(actual.ImportReservations ?? []); Assert.Empty(actual.FrozenObservers ?? []);
    }

    [Fact]
    public void FrozenSourceRetainsAnExactExportAcrossLearningAgingAndRestore() {
        var bank = Bank(); bank.Advance(100);
        bank.Observe(Evidence(1, tick: 100)); bank.Observe(Evidence(1, 1, 100, 9001));
        bank.Observe(Evidence(2, tick: 100, subject: 1));
        bank.Forget(Key(1, 9001)); // A forgotten impression's receipt also belongs to the held history.
        var expected = bank.CaptureObserver(Person(1));
        Assert.True(bank.TryFreezeObserver(Person(1), Transfer(), out var reason), reason);
        var hash = bank.StateHash;
        Assert.True(bank.TryFreezeObserver(Person(1), Transfer(), out reason), reason);
        Assert.False(bank.TryFreezeObserver(Person(1), Transfer(2), out reason)); Assert.Contains("different", reason);
        Assert.False(bank.ThawObserver(Person(1), Transfer(2))); Assert.False(bank.RetireFrozenObserver(Person(1), Transfer(2)));
        Assert.False(bank.Forget(Key(1))); Assert.Throws<InvalidOperationException>(() => bank.RemoveObserver(Person(1)));
        Assert.Throws<InvalidOperationException>(() => bank.CaptureFrozenObserver(Person(1), Transfer(2)));
        Assert.Equal(hash, bank.StateHash);
        Assert.Equal(WorldSocialEvidenceResult.ObserverFrozen, bank.Observe(Evidence(1, 2, 100)));
        bank.Advance(100000);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(2, 2, 100000, 1)));
        SameSnapshot(expected, bank.CaptureFrozenObserver(Person(1), Transfer()));
        Assert.Equal(99900UL, Read(bank, 1).AgeTicks); // Current read age differs intentionally from export age.
        var checkpoint = bank.Capture(); var restored = WorldSocialMemory.Restore(bank.Policy, checkpoint);
        Assert.Equal(bank.StateHash, restored.StateHash);
        ((WorldSocialFrozenObserverCheckpoint[])checkpoint.FrozenObservers!)[0] = default;
        SameSnapshot(expected, restored.CaptureFrozenObserver(Person(1), Transfer()));
        var other = Read(restored, 2, 1);
        Assert.True(restored.RetireFrozenObserver(Person(1), Transfer()));
        Assert.False(restored.RetireFrozenObserver(Person(1), Transfer()));
        Assert.False(Read(restored, 1).Known); Assert.Equal(other, Read(restored, 2, 1));
        Assert.Equal(1, restored.ReceiptCount); Assert.Equal(0, restored.FrozenObserverCount);
        Assert.Equal(restored.StateHash, WorldSocialMemory.Restore(restored.Policy, restored.Capture()).StateHash);
    }

    [Fact]
    public void FrozenOldestReceiptsDoNotBlockOtherExpiryAndThawDoesNotRefreshAnchors() {
        var bank = Bank(Policy() with { ExpiredReceiptsPerTick = 1 });
        bank.Observe(Evidence(1)); bank.Observe(Evidence(1, 1)); bank.Observe(Evidence(2)); bank.Observe(Evidence(3));
        Assert.True(bank.TryFreezeObserver(Person(1), Transfer(), out _));
        var held = bank.CaptureFrozenObserver(Person(1), Transfer());
        bank.Advance(50401); Assert.Equal(3, bank.ReceiptCount); Assert.Equal(1, bank.ReclaimedReceipts);
        bank.Advance(50402); Assert.Equal(2, bank.ReceiptCount); Assert.Equal(1, bank.ReclaimedReceipts);
        SameSnapshot(held, bank.CaptureFrozenObserver(Person(1), Transfer()));
        var restored = WorldSocialMemory.Restore(bank.Policy, bank.Capture());
        Assert.True(restored.ThawObserver(Person(1), Transfer())); Assert.False(restored.ThawObserver(Person(1), Transfer()));
        Assert.Equal(50402UL, Read(restored, 1).AgeTicks);
        Assert.All(restored.Capture().Receipts, row => Assert.Equal((Int128)0, row.LocalOccurredAt));
        restored.Advance(50402); Assert.Equal(2, restored.ReceiptCount); // Same boundary cannot earn extra reclamation.
        restored.Advance(50403); Assert.Equal(1, restored.ReceiptCount); Assert.Equal(1, restored.ReclaimedReceipts);
        restored.Advance(50404); Assert.Equal(0, restored.ReceiptCount);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, restored.Observe(Evidence(1, 2, 50404)));
    }

    [Fact]
    public void EmptyFrozenObserversHaveExclusiveBoundedAndCanonicalOwnership() {
        var bank = Bank(); var original = bank.StateHash;
        Assert.False(bank.TryFreezeObserver(default, Transfer(), out _));
        Assert.False(bank.TryFreezeObserver(Person(1), default, out _));
        for (var index = WorldSocialMemory.MaximumFrozenObservers - 1; index >= 0; index--) {
            Assert.True(bank.TryFreezeObserver(Person(index), Transfer(), out _));
        }
        var held = bank.StateHash;
        Assert.NotEqual(original, held);
        Assert.False(bank.TryFreezeObserver(Person(999), Transfer(), out _));
        Assert.True(bank.TryFreezeObserver(Person(0), Transfer(), out _));
        Assert.False(bank.TryReserveImport(Transfer(2), [new(Person(0), 0, 0)], out _));
        Assert.False(bank.TryImportObserver(Person(0), bank.Policy, Bank().Capture(), out _));
        Assert.Equal(held, bank.StateHash);
        var restored = WorldSocialMemory.Restore(bank.Policy, bank.Capture());
        Assert.Equal(held, restored.StateHash);
        Assert.Equal(Enumerable.Range(0, WorldSocialMemory.MaximumFrozenObservers), restored.Capture().FrozenObservers!.Select(row => row.Observer.Index));
        for (var index = 0; index < WorldSocialMemory.MaximumFrozenObservers; index++) { Assert.True(restored.RetireFrozenObserver(Person(index), Transfer())); }
        Assert.Equal(original, restored.StateHash);
        Assert.True(restored.TryReserveImport(Transfer(), [new(Person(0), 0, 0)], out _));
        Assert.False(restored.TryFreezeObserver(Person(0), Transfer(), out _));
    }

    [Fact]
    public void FrozenCheckpointRejectsMalformedHoldsLateRecordsAndImportedAuthorityState() {
        var bank = Bank(); bank.Advance(20); bank.Observe(Evidence(1, tick: 20));
        var state = bank.Capture(); var hash = bank.StateHash;
        WorldSocialFrozenObserverCheckpoint hold = new(Person(1), Transfer(), 20);
        IReadOnlyList<WorldSocialFrozenObserverCheckpoint>[] malformed = [
            [hold, hold], [hold with { Observer = default }], [hold with { Transfer = default }],
            [hold with { FrozenAt = 21 }], [hold with { FrozenAt = 19 }],
            new ClaimedRows<WorldSocialFrozenObserverCheckpoint>(int.MaxValue, []),
            new ClaimedRows<WorldSocialFrozenObserverCheckpoint>(-1, []),
            new ClaimedRows<WorldSocialFrozenObserverCheckpoint>(0, [hold]),
            new ClaimedRows<WorldSocialFrozenObserverCheckpoint>(1, []),
        ];
        foreach (var rows in malformed) {
            Assert.Throws<ArgumentException>(() => WorldSocialMemory.Restore(bank.Policy, state with { FrozenObservers = rows }));
            Assert.Equal(hash, bank.StateHash);
        }
        var receiptOnly = state with { Impressions = [], FrozenObservers = [hold with { FrozenAt = 19 }] };
        Assert.Throws<ArgumentException>(() => WorldSocialMemory.Restore(bank.Policy, receiptOnly));
        Assert.Throws<ArgumentException>(() => WorldSocialMemory.Restore(bank.Policy, state with {
            FrozenObservers = [new(Person(2), Transfer(), 20)], ImportReservations = [new(Transfer(2), [new(Person(2), 0, 0)])],
        }));
        var valid = WorldSocialMemory.Restore(bank.Policy, state with { FrozenObservers = [hold] });
        Assert.True(valid.IsObserverFrozen(Person(1)));
        var destination = Bank();
        Assert.False(destination.TryImportObserver(Person(1), bank.Policy, valid.Capture(), out var reason));
        Assert.Contains("ownership holds", reason);
        Assert.True(destination.TryImportObserver(Person(1), bank.Policy, valid.CaptureFrozenObserver(Person(1), Transfer()), out reason), reason);
    }

    [Fact]
    public void FreezeThawRetireAndConcurrentObserverLearningAllocateNothingAfterWarmup() {
        var bank = Bank(Policy(32, 32));
        static int Run(WorldSocialMemory memory, int iterations) {
            var successes = 0;
            for (var index = 0; index < iterations; index++) {
                memory.Advance(memory.EngineTick + 50401);
                for (var entry = 0; entry < 16; entry++) { memory.Observe(Evidence(1, (ulong)entry, memory.EngineTick, 9000 + entry)); }
                if (memory.TryFreezeObserver(Person(1), Transfer(), out _) && memory.ThawObserver(Person(1), Transfer()) &&
                    memory.TryFreezeObserver(Person(1), Transfer(), out _) &&
                    memory.Observe(Evidence(2, memory.EngineTick, memory.EngineTick)) == WorldSocialEvidenceResult.Accepted &&
                    memory.RetireFrozenObserver(Person(1), Transfer())) { successes++; }
                _ = memory.StateHash;
            }
            return successes;
        }
        Assert.Equal(1024, Run(bank, 1024));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var successes = Run(bank, 4096);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(4096, successes); Assert.Equal(0, allocated);
        Assert.Equal(1, bank.ImpressionCount); Assert.Equal(1, bank.ReceiptCount); Assert.Equal(0, bank.FrozenObserverCount);
    }
}
