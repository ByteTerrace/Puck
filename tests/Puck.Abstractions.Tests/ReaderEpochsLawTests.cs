using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="ReaderEpochs{T}"/>: storage retired while a reader holds is never handed back for reuse until
/// that reader's hold closes, however many epochs pass, while a hold taken after the retirement never delays it; and
/// entering and leaving a hold allocates nothing.
/// </summary>
public sealed class ReaderEpochsLawTests {
    private sealed class Block(int id) {
        public int Id { get; } = id;
    }

    [Fact]
    public void AnOldTablesReaderNeverSeesItsBlockReused() {
        var epochs = new ReaderEpochs<Block>();
        var reclaimed = new List<int>();
        var block = new Block(id: 1);
        // A reader takes an entry naming the block from the table current at its hold.
        var reader = epochs.Enter();

        // The owner drops the entry (a Retain or a write that replaced it) and retires the block.
        epochs.Retire(item: block);
        // Later readers come and go, and the owner reclaims at every batch start.
        for (var batch = 0; (batch < 8); batch++) {
            using (epochs.Enter()) { }
            _ = epochs.Reclaim(release: item => reclaimed.Add(item: item.Id));
            Assert.Empty(collection: reclaimed);
        }
        reader.Dispose();
        _ = epochs.Reclaim(release: item => reclaimed.Add(item: item.Id));
        Assert.Equal(expected: [1], actual: reclaimed);
        Assert.Equal(expected: 0, actual: epochs.Pending);
    }

    [Fact]
    public void AHoldTakenAfterTheRetirementDoesNotDelayIt() {
        var epochs = new ReaderEpochs<Block>();
        var reclaimed = new List<int>();
        var earlier = epochs.Enter();

        epochs.Retire(item: new Block(id: 7));
        // The reclaim that follows the retirement opens a new epoch; a hold taken in it reads only what the owner
        // publishes after the retirement, so once the earlier reader leaves the block is free while the later one reads.
        _ = epochs.Reclaim(release: item => reclaimed.Add(item: item.Id));
        using var later = epochs.Enter();

        earlier.Dispose();
        _ = epochs.Reclaim(release: item => reclaimed.Add(item: item.Id));
        Assert.Equal(expected: [7], actual: reclaimed);
    }

    [Fact]
    public async Task ConcurrentReadersNeverSeeABlockReclaimedUnderThem() {
        var epochs = new ReaderEpochs<Block>();
        var current = new Block(id: 0);
        var reclaimed = new System.Collections.Concurrent.ConcurrentDictionary<Block, bool>(comparer: ReferenceEqualityComparer.Instance);
        var violations = 0;
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(start: 0, count: 4).Select(_ => Task.Run(() => {
            while (!stop.IsCancellationRequested) {
                using var hold = epochs.Enter();
                var seen = Volatile.Read(location: ref current);

                for (var spin = 0; (spin < 64); spin++) {
                    if (reclaimed.ContainsKey(key: seen)) { _ = Interlocked.Increment(location: ref violations); }
                }
            }
        })).ToArray();

        for (var round = 1; (round <= 20_000); round++) {
            var previous = Interlocked.Exchange(location1: ref current, value: new Block(id: round));

            epochs.Retire(item: previous);
            _ = epochs.Reclaim(release: item => reclaimed[item] = true);
        }
        await stop.CancelAsync();
        await Task.WhenAll(tasks: readers);
        Assert.Equal(expected: 0, actual: violations);
    }

    [Fact]
    public void EnteringAndLeavingAHoldAllocatesNothing() {
        var epochs = new ReaderEpochs<Block>();

        static void Body(ReaderEpochs<Block> epochs) {
            using var hold = epochs.Enter();
        }
        Body(epochs: epochs);
        Assert.Equal(expected: 0L, actual: Counting.AllocationWindow.Least(window: () => Body(epochs: epochs)));
    }
}
