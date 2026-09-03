using System.Collections;
using System.Diagnostics;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

[Collection(ConsoleRedirectionCollection.Name)]
public sealed partial class WorldSocialImportReservationLawTests(ITestOutputHelper output) {
    private static WorldEntityAddress Person(int index) => new("origin", index, 1);
    private static WorldTransferKey Transfer(ulong id = 1) => new("upstream", id);
    private static WorldSocialPolicy Policy(int impressions = 16, int receipts = 32) => new(
        [new(WorldCellName.Parse("helpfulness"))], ImpressionCapacity: impressions, ImpressionsPerObserver: impressions,
        ReceiptCapacity: receipts, EvidenceAttemptsPerTick: 1024, ExpiredReceiptsPerTick: 32, EvidenceLifetimeSeconds: 1);
    private static WorldSocialMemory Bank(WorldSocialPolicy? policy = null) => new(CompiledWorldSocialPolicy.Compile(policy ?? Policy()));
    private static WorldSocialImpressionKey Key(int observer, int subject = 9000) => new(Person(observer), Person(subject), 0);
    private static WorldSocialEvidence Evidence(int observer, ulong sequence = 0, ulong tick = 0, int subject = 9000) =>
        new(Key(observer, subject), new(Person(subject), "help", sequence), tick, FixedQ4816.One.Value, FixedQ4816.One.Value);
    private static WorldSocialObserverImport Import(WorldSocialMemory source, int observer) => new(Person(observer), source.Policy, source.CaptureObserver(Person(observer)));
    private static WorldSocialImpression Read(WorldSocialMemory bank, int observer, int subject = 9000) {
        Assert.True(bank.TryRead(Key(observer, subject), out var impression)); return impression;
    }

    [Fact]
    public void ReservationRetryIsExactDetachedAndCancellationRestoresTheOriginalHash() {
        var bank = Bank(); var initial = bank.StateHash;
        WorldSocialImportAllowance[] members = [new(Person(1), 2, 3), new(Person(2), 0, 0)];
        Assert.True(bank.TryReserveImport(Transfer(), members, out var reason), reason);
        var held = bank.StateHash;
        Assert.NotEqual(initial, held);
        Assert.Equal(1, bank.ImportReservationCount); Assert.Equal(2, bank.ReservedObserverCount);
        Assert.Equal(2, bank.ReservedImpressionCount); Assert.Equal(3, bank.ReservedReceiptCount);
        Assert.True(bank.TryReserveImport(Transfer(), [.. members], out reason), reason);
        Assert.Equal(held, bank.StateHash);
        members[0] = members[0] with { Receipts = 4 };
        Assert.False(bank.TryReserveImport(Transfer(), members, out reason)); Assert.Contains("different", reason);
        Assert.False(bank.TryReserveImport(Transfer(2), [members[1]], out reason)); Assert.Contains("owned or reserved", reason);
        Assert.False(bank.TryReserveImport(Transfer(2), [new(Person(3), 1, 1), new(Person(3), 1, 1)], out _));
        Assert.Equal(held, bank.StateHash);
        Assert.False(bank.CancelImportReservation(Transfer(99)));
        Assert.True(bank.CancelImportReservation(Transfer())); Assert.False(bank.CancelImportReservation(Transfer()));
        Assert.Equal(initial, bank.StateHash);
    }

    [Fact]
    public void LearningAndUnreservedImportsCannotStealHeldMemoryOrEmptyObserverClaims() {
        var source = Bank(); source.Observe(Evidence(1)); source.Observe(Evidence(4));
        var bank = Bank(Policy(3, 8));
        Assert.True(bank.TryReserveImport(Transfer(), [new(Person(1), 2, 2), new(Person(2), 0, 0)], out var reason), reason);
        Assert.Equal(WorldSocialEvidenceResult.ObserverReserved, bank.Observe(Evidence(2)));
        Assert.False(bank.TryImportObserver(Person(1), source.Policy, source.CaptureObserver(Person(1)), out reason));
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(3)));
        Assert.Equal(WorldSocialEvidenceResult.ImpressionCapacityLimited, bank.Observe(Evidence(4)));
        Assert.False(bank.TryImportObserver(Person(4), source.Policy, source.CaptureObserver(Person(4)), out _));
        Assert.True(bank.TryImportReserved(Transfer(), [Import(source, 1), Import(source, 2)], out reason), reason);
        Assert.Equal(0, bank.ImportReservationCount); Assert.Equal(0, bank.ReservedObserverCount);
        Assert.Equal(2, bank.ImpressionCount);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, bank.Observe(Evidence(4)));

        var receiptBank = Bank(Policy(8, 3));
        Assert.True(receiptBank.TryReserveImport(Transfer(), [new(Person(1), 1, 2)], out reason), reason);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, receiptBank.Observe(Evidence(3)));
        Assert.Equal(WorldSocialEvidenceResult.ReceiptCapacityLimited, receiptBank.Observe(Evidence(3, 1)));
        Assert.True(receiptBank.TryImportReserved(Transfer(), [Import(source, 1)], out reason), reason);
        Assert.Equal(WorldSocialEvidenceResult.Accepted, receiptBank.Observe(Evidence(3, 1)));
    }

    [Fact]
    public void LateMalformedOrOverflowingMemberCannotPartiallyImportAReservedGroup() {
        var source = Bank(); source.Observe(Evidence(1)); source.Observe(Evidence(2));
        var bank = Bank(); bank.Observe(Evidence(3));
        Assert.True(bank.TryReserveImport(Transfer(), [new(Person(1), 1, 1), new(Person(2), 1, 1)], out var reason), reason);
        var hash = bank.StateHash; var first = Import(source, 1); var second = Import(source, 2);
        var malformed = second with { Memory = second.Memory with { Impressions = [second.Memory.Impressions[0] with { Value = long.MaxValue }] } };
        var overflowing = second with { Memory = second.Memory with {
            NextOrdinal = ulong.MaxValue,
            Impressions = [second.Memory.Impressions[0] with { IndependentEvents = ulong.MaxValue }],
        } };
        foreach (var last in new[] { malformed, overflowing, second with { Observer = Person(4) }, default }) {
            Assert.False(bank.TryImportReserved(Transfer(), [first, last], out reason));
            Assert.Equal(hash, bank.StateHash);
            Assert.False(Read(bank, 1).Known); Assert.False(Read(bank, 2).Known);
            Assert.Equal(2, bank.ReservedObserverCount); Assert.Equal(1, bank.ImpressionCount);
        }
        Assert.False(bank.TryImportReserved(Transfer(), [second, first], out _));
        Assert.False(bank.TryImportReserved(Transfer(), [first], out _));
        Assert.True(bank.TryImportReserved(Transfer(), [first, second], out reason), reason);
        Assert.Equal(3, bank.ImpressionCount); Assert.Equal(0, bank.ReservedObserverCount);
        Assert.False(bank.TryImportReserved(Transfer(), [first, second], out reason)); Assert.Contains("missing", reason);
        var captured = bank.Capture();
        Assert.Equal(3UL, captured.NextOrdinal);
        Assert.Equal(3, captured.Receipts.Select(row => row.Ordinal).Distinct().Count());
        Assert.Equal(bank.StateHash, WorldSocialMemory.Restore(bank.Policy, captured).StateHash);
    }

    [Fact]
    public void EachMemberMustFitItsOwnQuotaAndClockRebasingPreservesReceiptOnlyOwners() {
        var source = Bank(); source.Advance(100000);
        source.Observe(Evidence(1, tick: 100000)); source.Observe(Evidence(2, tick: 100000)); source.Forget(Key(2));
        source.Advance(100010);
        var bank = Bank();
        Assert.True(bank.TryReserveImport(Transfer(), [new(Person(1), 0, 1), new(Person(2), 1, 1)], out var reason), reason);
        var hash = bank.StateHash;
        Assert.False(bank.TryImportReserved(Transfer(), [Import(source, 1), Import(source, 2)], out _));
        Assert.Equal(hash, bank.StateHash); bank.CancelImportReservation(Transfer());
        Assert.True(bank.TryReserveImport(Transfer(), [new(Person(1), 1, 1), new(Person(2), 0, 1)], out reason), reason);
        bank.Advance(7);
        Assert.True(bank.TryImportReserved(Transfer(), [Import(source, 1), Import(source, 2)], out reason), reason);
        Assert.Equal(10UL, Read(bank, 1).AgeTicks); Assert.False(Read(bank, 2).Known);
        Assert.Equal(2, bank.ReceiptCount);
        Assert.Equal(-3, (long)bank.Capture().Receipts[0].LocalOccurredAt);
        Assert.All(bank.Capture().Receipts, receipt => Assert.Equal(100000UL, receipt.OccurredAt));
        Assert.Equal(WorldSocialEvidenceResult.Duplicate, bank.Observe(Evidence(2, tick: 100000) with { LocalOccurredAt = -3 }));
        Assert.False(bank.TryReserveImport(Transfer(2), [new(Person(2), 0, 0)], out reason));
        Assert.Contains("owned", reason);
    }

    [Fact]
    public void CheckpointsPreserveCanonicalReservationsAndDoNotAliasCallerLists() {
        var left = Bank(); var right = Bank();
        WorldSocialImportAllowance[] a = [new(Person(1), 2, 2)];
        WorldSocialImportAllowance[] b = [new(Person(2), 3, 4)];
        left.TryReserveImport(Transfer(2), b, out _); left.TryReserveImport(Transfer(1), a, out _);
        right.TryReserveImport(Transfer(1), a, out _); right.TryReserveImport(Transfer(2), b, out _);
        Assert.Equal(left.StateHash, right.StateHash);
        var checkpoint = left.Capture();
        Assert.Equal(new ulong[] { 1, 2 }, checkpoint.ImportReservations!.Select(row => row.Key.TransferId));
        var restored = WorldSocialMemory.Restore(left.Policy, checkpoint);
        Assert.Equal(left.StateHash, restored.StateHash);
        ((WorldSocialImportAllowance[])checkpoint.ImportReservations![0].Members)[0] = new(Person(99), 0, 0);
        Assert.Equal(left.StateHash, restored.StateHash);
        Assert.True(restored.TryReserveImport(Transfer(1), a, out _));
        Assert.True(restored.CancelImportReservation(Transfer(2)));
        Assert.NotEqual(left.StateHash, restored.StateHash);
        left.Advance(ulong.MaxValue); restored.Advance(ulong.MaxValue);
        Assert.Equal(2, left.ImportReservationCount); Assert.Equal(1, restored.ImportReservationCount);
    }

    [Fact]
    public void InvalidReservationCheckpointsAndLyingCollectionsRefuseWithoutTouchingLiveState() {
        var bank = Bank(Policy(4, 4)); bank.Observe(Evidence(1)); bank.Forget(Key(1));
        var state = bank.Capture(); var hash = bank.StateHash;
        WorldSocialImportReservationCheckpoint valid = new(Transfer(), [new(Person(2), 2, 2)]);
        IReadOnlyList<WorldSocialImportReservationCheckpoint>[] malformed = [
            [valid, valid], [valid with { Key = default }], [valid with { Members = null! }], [null!],
            [valid with { Members = [] }], [valid with { Members = [new(Person(1), 0, 0)] }],
            [valid with { Members = [new(Person(2), -1, 1)] }],
            [valid with { Members = [new(Person(2), 2, 2), new(Person(2), 1, 1)] }],
            [valid, new(Transfer(2), [new(Person(3), 3, 1)])],
            [valid, new(Transfer(2), [new(Person(3), 1, 2)])],
            [valid with { Members = new ClaimedRows<WorldSocialImportAllowance>(0, [new(Person(2), 0, 0)]) }],
            new ClaimedRows<WorldSocialImportReservationCheckpoint>(-1, []),
            new ClaimedRows<WorldSocialImportReservationCheckpoint>(0, [valid]),
            new ClaimedRows<WorldSocialImportReservationCheckpoint>(1, []),
        ];
        foreach (var rows in malformed) {
            Assert.Throws<ArgumentException>(() => WorldSocialMemory.Restore(bank.Policy, state with { ImportReservations = rows }));
            Assert.Equal(hash, bank.StateHash);
        }
        Assert.False(bank.TryReserveImport(Transfer(), new ClaimedRows<WorldSocialImportAllowance>(1, []), out _));
        Assert.Equal(hash, bank.StateHash);
        var control = WorldSocialMemory.Restore(bank.Policy, state with { ImportReservations = [valid] });
        Assert.Equal(2, control.ReservedReceiptCount);
        Assert.True(control.TryReserveImport(Transfer(), valid.Members, out _));
        Assert.False(control.TryImportReserved(Transfer(), new ClaimedRows<WorldSocialObserverImport>(1, []), out _));
        Assert.False(bank.TryImportObserver(Person(2), bank.Policy, control.Capture(), out _));
    }

    [Fact]
    public void ChangingCollectionCountsCannotGrowAllocationsOrCommitAnEmptySubstitute() {
        var bank = Bank(); var hash = bank.StateHash;
        Assert.False(bank.TryReserveImport(Transfer(), new ChangingRows<WorldSocialImportAllowance>(1, int.MaxValue), out _));
        Assert.Equal(hash, bank.StateHash);
        Assert.False(bank.TryReserveImport(Transfer(), new ChangingRows<WorldSocialImportAllowance>(1, 0), out _));
        Assert.Equal(hash, bank.StateHash);
        Assert.True(bank.TryReserveImport(Transfer(), [new(Person(1), 0, 0)], out _));
        hash = bank.StateHash;
        Assert.False(bank.TryImportReserved(Transfer(), new ChangingRows<WorldSocialObserverImport>(1, int.MaxValue), out _));
        Assert.False(bank.TryImportReserved(Transfer(), new ChangingRows<WorldSocialObserverImport>(1, 0), out _));
        Assert.Equal(hash, bank.StateHash);
        var checkpoint = Bank().Capture() with { Impressions = new ChangingRows<WorldSocialImpressionCheckpoint>(2, int.MaxValue) };
        Assert.Throws<ArgumentException>(() => WorldSocialMemory.Restore(bank.Policy, checkpoint));
        var source = Bank();
        Assert.True(bank.TryImportReserved(Transfer(), [Import(source, 1)], out var reason), reason);
        Assert.Equal(0, bank.ImportReservationCount);
    }

    [Fact]
    public void DetachedHistoryMustStillFitTheDestinationQuotaAfterCallerCountsChange() {
        var source = Bank(); source.Observe(Evidence(1)); source.Observe(Evidence(1, 1, subject: 9001));
        var exported = source.CaptureObserver(Person(1));
        var bank = Bank();
        Assert.True(bank.TryReserveImport(Transfer(), [new(Person(1), 1, 2)], out var reason), reason);
        var hash = bank.StateHash;
        var growingImpressions = exported with {
            Impressions = new ChangingCountRows<WorldSocialImpressionCheckpoint>(3, exported.Impressions.ToArray()),
        };
        Assert.False(bank.TryImportReserved(Transfer(), [new(Person(1), source.Policy, growingImpressions)], out reason));
        Assert.Contains("impression capacity", reason); Assert.Equal(hash, bank.StateHash);
        bank.CancelImportReservation(Transfer());
        Assert.True(bank.TryReserveImport(Transfer(), [new(Person(1), 2, 1)], out reason), reason);
        hash = bank.StateHash;
        var growingReceipts = exported with {
            Receipts = new ChangingCountRows<WorldSocialReceiptCheckpoint>(2, exported.Receipts.ToArray()),
        };
        Assert.False(bank.TryImportReserved(Transfer(), [new(Person(1), source.Policy, growingReceipts)], out reason));
        Assert.Contains("receipt capacity", reason); Assert.Equal(hash, bank.StateHash);
        var small = Bank(Policy(1, 4));
        var smallHash = small.StateHash;
        Assert.False(small.TryImportObserver(Person(1), source.Policy, exported with {
            Impressions = new ChangingCountRows<WorldSocialImpressionCheckpoint>(3, exported.Impressions.ToArray()),
        }, out _));
        Assert.Equal(smallHash, small.StateHash);
        bank.CancelImportReservation(Transfer());
        Assert.True(bank.TryReserveImport(Transfer(), [new(Person(1), 2, 2)], out reason), reason);
        Assert.True(bank.TryImportReserved(Transfer(), [new(Person(1), source.Policy, exported)], out reason), reason);
    }

    [Fact]
    public void EmptyReservationsAreBoundedEvenWhenTheyReserveNoRecordStorage() {
        var bank = Bank(Policy(1, 1));
        for (var index = 0; index < WorldSocialMemory.MaximumReservedObservers; index++) {
            Assert.True(bank.TryReserveImport(Transfer((ulong)index), [new(Person(index), 0, 0)], out var reason), reason);
        }
        var hash = bank.StateHash;
        Assert.False(bank.TryReserveImport(Transfer(9999), [new(Person(9999), 0, 0)], out _));
        Assert.Equal(hash, bank.StateHash); Assert.Equal(0, bank.ReservedImpressionCount);
        bank.CancelImportReservation(Transfer(0));
        Assert.True(bank.TryReserveImport(Transfer(9999), [new(Person(9999), 0, 0)], out _));
        Assert.Equal(bank.StateHash, WorldSocialMemory.Restore(bank.Policy, bank.Capture()).StateHash);
    }

    [Fact]
    public void OrdinaryLearningHashAndExpiryStayAllocationFreeWhileReservationsAreHeld() {
        var bank = Bank(Policy(8, 16));
        bank.TryReserveImport(Transfer(), [new(Person(1), 4, 8)], out _);
        static int Run(WorldSocialMemory memory, int count) {
            var accepted = 0;
            for (var index = 0; index < count; index++) {
                memory.Advance(memory.EngineTick + 50401);
                if (memory.Observe(Evidence(3, memory.EngineTick, memory.EngineTick)) == WorldSocialEvidenceResult.Accepted) { accepted++; }
                _ = memory.StateHash;
            }
            return accepted;
        }
        Assert.Equal(4096, Run(bank, 4096));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var accepted = Run(bank, 16384);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(16384, accepted); Assert.Equal(0, allocated);
        Assert.Equal(1, bank.ReceiptCount); Assert.Equal(8, bank.ReservedReceiptCount);
    }

    [Fact]
    public void Dense4096ObserverReservedGroupRoundTripPreservesAllHistories() {
        const int observers = 4096, entries = 16;
        var policy = Policy(observers * entries, observers * entries) with { ImpressionsPerObserver = entries, EvidenceAttemptsPerTick = observers * entries };
        var source = Bank(policy); var target = Bank(policy);
        source.Advance(100010);
        for (var observer = 0; observer < observers; observer++) {
            for (var entry = 0; entry < entries; entry++) {
                Assert.Equal(WorldSocialEvidenceResult.Accepted, source.Observe(Evidence(observer, (ulong)entry, 100000, 9000 + entry)));
            }
        }
        var sourceExpected = source.Capture().Impressions.Select(row => (row.Key, Read(source, row.Key.Observer.Index, row.Key.Subject.Index))).ToArray();
        var started = Stopwatch.GetTimestamp(); var before = GC.GetAllocatedBytesForCurrentThread();
        static void Move(WorldSocialMemory from, WorldSocialMemory to, bool reverse) {
            const int groupSize = 64;
            for (var batch = 0; batch < observers / groupSize; batch++) {
                var members = Enumerable.Range(batch * groupSize, groupSize).Select(i => reverse ? observers - 1 - i : i).ToArray();
                var allowances = members.Select(i => new WorldSocialImportAllowance(Person(i), entries, entries)).ToArray();
                foreach (var member in members) { Assert.True(from.TryFreezeObserver(Person(member), Transfer((ulong)batch), out _)); }
                Assert.True(to.TryReserveImport(Transfer((ulong)batch), allowances, out var reason), reason);
                var incoming = members.Select(i => new WorldSocialObserverImport(Person(i), from.Policy, from.CaptureFrozenObserver(Person(i), Transfer((ulong)batch)))).ToArray();
                Assert.True(to.TryImportReserved(Transfer((ulong)batch), incoming, out reason), reason);
                foreach (var member in members) { Assert.True(from.RetireFrozenObserver(Person(member), Transfer((ulong)batch))); }
            }
        }
        Move(source, target, false); Move(target, source, true);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"4096 observers x16 entries, reserved batches of64, out+back: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}ms / {allocated}B (component cold-path, not frame time)");
        Assert.Equal(observers * entries, source.ImpressionCount); Assert.Equal(observers * entries, source.ReceiptCount);
        Assert.Equal(0, target.ImpressionCount); Assert.Equal(0, target.ReceiptCount);
        Assert.Equal(0, source.ImportReservationCount); Assert.Equal(0, target.ImportReservationCount);
        Assert.Equal(0, source.FrozenObserverCount); Assert.Equal(0, target.FrozenObserverCount);
        foreach (var (key, expected) in sourceExpected) { Assert.Equal(expected, Read(source, key.Observer.Index, key.Subject.Index)); }
        Assert.Equal(source.StateHash, WorldSocialMemory.Restore(source.Policy, source.Capture()).StateHash);
    }

    private sealed class ClaimedRows<T>(int count, T[] rows) : IReadOnlyList<T> {
        public int Count => count;
        public T this[int index] => rows[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)rows).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ChangingRows<T>(int readsBeforeChange, int changedCount) : IReadOnlyList<T> {
        private int m_reads;
        public int Count => ++m_reads <= readsBeforeChange ? 1 : changedCount;
        public T this[int index] => throw new InvalidOperationException();
        public IEnumerator<T> GetEnumerator() { yield break; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ChangingCountRows<T>(int smallReads, T[] rows) : IReadOnlyList<T> {
        private int m_reads;
        public int Count => ++m_reads <= smallReads ? 1 : rows.Length;
        public T this[int index] => rows[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)rows).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
