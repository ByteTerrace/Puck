using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class GpuChangedRowsLawTests {
    [Fact]
    public void CapturedEndpointsExcludeLaterChangesAndFailedWindowsAppendNoPartialReseats() {
        var journal = new GpuChangedRows(capacity: 8, history: 4, trackReseats: true);
        var changed = new GpuUploadRuns(capacity: 8);
        var reseated = new GpuUploadRuns(capacity: 8);

        journal.Begin(everything: true, rows: 32);
        journal.Begin(everything: false, rows: 32);
        journal.Owe(count: 2, reseat: true, start: 2);
        var captured = journal.Serial;

        journal.Begin(everything: false, rows: 32);
        journal.Owe(count: 1, reseat: true, start: 8);
        reseated.Add(length: 1, start: 0);

        Assert.True(condition: Window(into: changed, journal: journal, reseated: reseated, rows: 32, since: 1, through: captured));
        Assert.Equal(new[] { 2, 3 }, Rows(runs: changed));
        Assert.Equal(new[] { 0, 2, 3 }, Rows(runs: reseated));

        reseated.Clear();
        Assert.True(condition: Window(into: changed, journal: journal, reseated: reseated, rows: 32, since: 1, through: captured));
        Assert.Equal(new[] { 2, 3 }, Rows(runs: reseated));

        journal.Begin(everything: true, rows: 32);
        foreach (var window in new[] { (1L, 4L), (2L, 5L), (4L, 3L), (-1L, 4L) }) {
            changed.Add(length: 1, start: 20);
            reseated.Clear();
            reseated.Add(length: 1, start: 0);
            Assert.False(condition: Window(into: changed, journal: journal, reseated: reseated, rows: 32, since: window.Item1, through: window.Item2));
            Assert.Equal(Enumerable.Range(count: 32, start: 0), Rows(runs: changed));
            Assert.Equal(new[] { 0 }, Rows(runs: reseated));
            Assert.False(condition: Window(into: changed, journal: journal, reseated: reseated, rows: 32, since: window.Item1, through: window.Item2));
            Assert.Equal(new[] { 0 }, Rows(runs: reseated));
        }
        journal.Begin(everything: false, rows: 32);
        Assert.False(condition: Window(journal, 1, captured, 32, changed));
        Assert.True(condition: Window(journal, journal.Serial, journal.Serial, 32, changed));
        Assert.Empty(collection: Rows(runs: changed));
    }
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(92)]
    [Theory]
    public void SkippedConsumerWindowsMatchAnIndependentRowSetAcrossRingRotation(int seed) {
        const int RowCount = 31, History = 6;
        var journal = new GpuChangedRows(capacity: 32, history: History, trackReseats: true);
        var changed = new GpuUploadRuns(capacity: 64);
        var reseated = new GpuUploadRuns(capacity: 64);
        var frames = new List<(bool Full, HashSet<int> Changed, HashSet<int> Reseated)>();
        var random = new Random(Seed: seed);

        for (var frame = 1; (frame <= 48); frame++) {
            var full = ((frame % 13) == 0);
            var rows = new HashSet<int>();
            var seats = new HashSet<int>();

            journal.Begin(everything: full, rows: RowCount);
            for (var write = 0; (write < 3); write++) {
                var start = random.Next(maxValue: (RowCount - 3));
                var count = random.Next(maxValue: 4, minValue: 1);

                journal.Owe(count: count, reseat: (write == 0), start: start);
                foreach (var row in Enumerable.Range(count: count, start: start)) { rows.Add(item: row); if (write == 0) { seats.Add(item: row); } }
            }
            frames.Add(item: (full, rows, seats));
            for (var gap = 0; (gap <= (History + 1)); gap++) {
                var since = Math.Max(val1: 0, val2: (frame - gap));
                var through = Math.Max(val1: since, val2: (frame - 1));
                var window = frames.Skip(count: since).Take(count: (through - since)).ToArray();
                var available = ((since > 0) && ((frame - since) < History) && window.All(predicate: value => !value.Full));

                changed.Add(length: 1, start: 30);
                reseated.Clear();
                reseated.Add(length: 1, start: 30);

                Assert.Equal(available, Window(into: changed, journal: journal, reseated: reseated, rows: RowCount, since: since, through: through));
                Assert.Equal((available ? window.SelectMany(selector: value => value.Changed).Distinct().Order().ToArray() : Enumerable.Range(count: RowCount, start: 0).ToArray()), Rows(runs: changed));
                Assert.Equal((available ? window.SelectMany(selector: value => value.Reseated).Append(element: 30).Distinct().Order().ToArray() : [30]), Rows(runs: reseated));
            }
        }
    }
    [Fact]
    public void BoundedCoarseningRetainsEveryChangedRowAndContainsUsesTheRepresentedHalfOpenRanges() {
        var journal = new GpuChangedRows(history: 4, capacity: 4);
        var collected = new GpuUploadRuns(capacity: 16);

        journal.Begin(everything: true, rows: 600);
        journal.Begin(everything: false, rows: 600);
        var dirty = Enumerable.Range(count: 80, start: 0).Select(selector: value => (value * 7)).ToArray();

        foreach (var row in dirty) { journal.Owe(start: row, count: 1); }

        Assert.True(condition: Window(journal, 1, 2, 600, collected));
        Assert.InRange(collected.Count, 1, 4);
        Assert.All(dirty, row => Assert.True(condition: collected.Contains(value: row)));
        for (var point = -1; (point <= 600); point++) {
            var inRepresentedRange = Enumerable.Range(0, collected.Count).Any(predicate: run => ((point >= collected.Start(index: run)) && (point < (((long)collected.Start(index: run)) + collected.Length(index: run)))));

            Assert.Equal(inRepresentedRange, collected.Contains(value: point));
        }

        collected.Clear();
        collected.Add(length: 2, start: 3);
        collected.Add(length: 3, start: 9);
        collected.Add(length: 3, start: (int.MaxValue - 3));
        foreach (var point in new[] { int.MinValue, -1, 2, 3, 4, 5, 8, 9, 11, 12, (int.MaxValue - 4), (int.MaxValue - 3), (int.MaxValue - 1), int.MaxValue }) {
            var expected = (((point >= 3) && (point < 5)) || ((point >= 9) && (point < 12)) || ((point >= (int.MaxValue - 3)) && (point < int.MaxValue)));

            Assert.Equal(expected, collected.Contains(value: point));
        }
    }

    private static int[] Rows(GpuUploadRuns runs) => Enumerable.Range(0, runs.Count)
        .SelectMany(selector: run => Enumerable.Range(runs.Start(index: run), runs.Length(index: run))).ToArray();
    private static bool Window(GpuChangedRows journal, long since, long through, int rows, GpuUploadRuns into, GpuUploadRuns? reseated = null) {
        var cursor = new GpuRowCursor();

        _ = cursor.Collect(journal.Reader, since, journal, rows, 0, new GpuUploadRuns(2));
        cursor.Commit();
        return !cursor.Collect(journal.Reader, through, journal, rows, 0, into, reseated);
    }

    [Fact]
    public void CursorRefusesAnotherReaderRosterExtentAndARevisionWithoutAnEndpointAdvance() {
        var journal = new GpuChangedRows(4);
        var cursor = new GpuRowCursor();
        var table = new object();
        var runs = new GpuUploadRuns(8);

        journal.Begin(everything: false, rows: 4);
        Assert.True(condition: cursor.Collect(journal.Reader, journal.Serial, table, 4, 0, runs));
        cursor.Commit();
        Assert.False(condition: cursor.Collect(journal.Reader, journal.Serial, table, 4, 0, runs));
        Assert.Empty(collection: Rows(runs: runs));
        Assert.True(condition: cursor.Collect(journal.Reader, journal.Serial, table, 4, 1, runs));
        Assert.True(condition: cursor.Collect(journal.Reader, journal.Serial, new object(), 4, 0, runs));
        Assert.True(condition: cursor.Collect(new GpuChangedRows(4).Reader, journal.Serial, table, 4, 0, runs));
        journal.Begin(everything: false, rows: 2);
        Assert.True(condition: cursor.Collect(journal.Reader, journal.Serial, table, 2, 0, runs));
        Assert.Equal(new[] { 0, 1 }, Rows(runs: runs));
    }
}
