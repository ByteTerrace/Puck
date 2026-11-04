using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class GpuChangedRowsLawTests {
    [Fact]
    public void CapturedEndpointsExcludeLaterChangesAndFailedWindowsAppendNoPartialReseats() {
        var journal = new GpuChangedRows(history: 4, capacity: 8, trackReseats: true);
        var changed = new GpuUploadRuns(capacity: 8);
        var reseated = new GpuUploadRuns(capacity: 8);
        journal.Begin(everything: true, rows: 32);
        journal.Begin(everything: false, rows: 32);
        journal.Owe(start: 2, count: 2, reseat: true);
        var captured = journal.Serial;
        journal.Begin(everything: false, rows: 32);
        journal.Owe(start: 8, count: 1, reseat: true);
        reseated.Add(start: 0, length: 1);

        Assert.True(journal.TryCollect(since: 1, through: captured, into: changed, reseated: reseated));
        Assert.Equal(new[] { 2, 3 }, Rows(changed));
        Assert.Equal(new[] { 0, 2, 3 }, Rows(reseated));

        reseated.Clear();
        Assert.True(journal.TryCollect(since: 1, through: captured, into: null, reseated: reseated));
        Assert.Equal(new[] { 2, 3 }, Rows(reseated));

        journal.Begin(everything: true, rows: 32);
        foreach (var window in new[] { (1L, 4L), (2L, 5L), (4L, 3L), (-1L, 4L) }) {
            changed.Add(start: 20, length: 1);
            reseated.Clear();
            reseated.Add(start: 0, length: 1);
            Assert.False(journal.TryCollect(since: window.Item1, through: window.Item2, into: changed, reseated: reseated));
            Assert.Empty(Rows(changed));
            Assert.Equal(new[] { 0 }, Rows(reseated));
            Assert.False(journal.TryCollect(since: window.Item1, through: window.Item2, into: null, reseated: reseated));
            Assert.Equal(new[] { 0 }, Rows(reseated));
        }
        journal.Begin(everything: false, rows: 32);
        Assert.False(journal.TryCollect(since: 1, through: captured, into: changed));
        Assert.True(journal.TryCollect(since: journal.Serial, through: journal.Serial, into: changed));
        Assert.Empty(Rows(changed));
    }

    [InlineData(5)]
    [InlineData(17)]
    [InlineData(92)]
    [Theory]
    public void SkippedConsumerWindowsMatchAnIndependentRowSetAcrossRingRotation(int seed) {
        const int RowCount = 31, History = 6;
        var journal = new GpuChangedRows(history: History, capacity: 32, trackReseats: true);
        var changed = new GpuUploadRuns(capacity: 64);
        var reseated = new GpuUploadRuns(capacity: 64);
        var frames = new List<(bool Full, HashSet<int> Changed, HashSet<int> Reseated)>();
        var random = new Random(seed);

        for (var frame = 1; frame <= 48; frame++) {
            var full = ((frame % 13) == 0);
            var rows = new HashSet<int>();
            var seats = new HashSet<int>();
            journal.Begin(everything: full, rows: RowCount);
            for (var write = 0; write < 3; write++) {
                var start = random.Next(RowCount - 3);
                var count = random.Next(1, 4);
                journal.Owe(start: start, count: count, reseat: write == 0);
                foreach (var row in Enumerable.Range(start, count)) { rows.Add(row); if (write == 0) { seats.Add(row); } }
            }
            frames.Add((full, rows, seats));
            for (var gap = 0; gap <= History + 1; gap++) {
                var since = Math.Max(0, frame - gap);
                var through = Math.Max(since, frame - 1);
                var window = frames.Skip(since).Take(through - since).ToArray();
                var available = ((since > 0) && ((frame - since) < History) && window.All(value => !value.Full));
                changed.Add(start: 30, length: 1);
                reseated.Clear();
                reseated.Add(start: 30, length: 1);

                Assert.Equal(available, journal.TryCollect(since: since, through: through, into: changed, reseated: reseated));
                Assert.Equal(available ? window.SelectMany(value => value.Changed).Distinct().Order().ToArray() : [], Rows(changed));
                Assert.Equal(available ? window.SelectMany(value => value.Reseated).Append(30).Distinct().Order().ToArray() : [30], Rows(reseated));
            }
        }
    }

    [Fact]
    public void BoundedCoarseningRetainsEveryChangedRowAndContainsUsesTheRepresentedHalfOpenRanges() {
        var journal = new GpuChangedRows(history: 4, capacity: 4);
        var collected = new GpuUploadRuns(capacity: 16);
        journal.Begin(everything: true, rows: 600);
        journal.Begin(everything: false, rows: 600);
        var dirty = Enumerable.Range(0, 80).Select(value => value * 7).ToArray();
        foreach (var row in dirty) { journal.Owe(start: row, count: 1); }

        Assert.True(journal.TryCollect(since: 1, through: 2, into: collected));
        Assert.InRange(collected.Count, 1, 4);
        Assert.All(dirty, row => Assert.True(collected.Contains(row)));
        for (var point = -1; point <= 600; point++) {
            var inRepresentedRange = Enumerable.Range(0, collected.Count).Any(run => point >= collected.Start(run) && point < (long)collected.Start(run) + collected.Length(run));
            Assert.Equal(inRepresentedRange, collected.Contains(point));
        }

        collected.Clear();
        collected.Add(start: 3, length: 2);
        collected.Add(start: 9, length: 3);
        collected.Add(start: int.MaxValue - 3, length: 3);
        foreach (var point in new[] { int.MinValue, -1, 2, 3, 4, 5, 8, 9, 11, 12, int.MaxValue - 4, int.MaxValue - 3, int.MaxValue - 1, int.MaxValue }) {
            var expected = ((point >= 3) && (point < 5)) || ((point >= 9) && (point < 12)) || ((point >= int.MaxValue - 3) && (point < int.MaxValue));
            Assert.Equal(expected, collected.Contains(point));
        }
    }

    private static int[] Rows(GpuUploadRuns runs) => Enumerable.Range(0, runs.Count)
        .SelectMany(run => Enumerable.Range(runs.Start(run), runs.Length(run))).ToArray();
}
