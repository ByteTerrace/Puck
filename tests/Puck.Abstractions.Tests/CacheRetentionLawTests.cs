using Puck.Testing;

namespace Puck.Abstractions.Tests;

/// <summary>The one retention policy of Puck's on-disk caches (<see cref="CacheRetention"/>): least recently used out
/// under a count and a byte bound, the entry in use always kept and counted first, equal stamps falling to the ordinally
/// lesser path, an age bound that removes what no one used since, and an enforcement that deletes only the files its
/// owner names as entries and leaves one a reader holds.</summary>
public sealed class CacheRetentionLawTests {
    private static readonly DateTime Now = new(day: 8, hour: 12, kind: DateTimeKind.Utc, minute: 0, month: 10, second: 0, year: 2026);

    private static CacheEntry Entry(string name, int minutesAgo, long bytes = 0) =>
        new(Bytes: bytes, LastUsedUtc: Now.AddMinutes(value: -minutesAgo), Path: $"/cache/{name}");
    private static string[] Names(IEnumerable<CacheEntry> entries) => [.. entries.Select(selector: static entry => entry.Path[7..])];

    [Fact]
    public void TheMostRecentlyUsedEntriesWithinTheCountStay() {
        var evicted = CacheRetention.SelectEvictions(
            bound: CacheBound.Entries(maxEntries: 2),
            entries: [Entry(name: "old", minutesAgo: 30), Entry(name: "new", minutesAgo: 1), Entry(name: "b", minutesAgo: 10), Entry(name: "a", minutesAgo: 10)]
        );

        // "a" and "b" share a stamp; the ordinally lesser path stays.
        Assert.Equal(actual: Names(entries: evicted), expected: ["b", "old"]);
    }
    [Fact]
    public void FromTheFirstEntryPastTheByteBoundEveryOlderOneGoes() {
        var evicted = CacheRetention.SelectEvictions(
            bound: new CacheBound(MaxBytes: 100, MaxEntries: 10),
            entries: [Entry(bytes: 60, minutesAgo: 1, name: "newest"), Entry(bytes: 50, minutesAgo: 2, name: "large"), Entry(bytes: 10, minutesAgo: 3, name: "small")]
        );

        // "small" would fit beside "newest", but it is older than the entry that did not fit.
        Assert.Equal(actual: Names(entries: evicted), expected: ["large", "small"]);
    }
    [Fact]
    public void TheEntryInUseStaysWhateverItsStampAndTakesItsPlaceFirst() {
        var evicted = CacheRetention.SelectEvictions(
            bound: CacheBound.Entries(maxEntries: 2),
            entries: [Entry(name: "used", minutesAgo: 90), Entry(name: "recent", minutesAgo: 1), Entry(name: "older", minutesAgo: 5)],
            inUse: "/cache/used"
        );

        Assert.Equal(actual: Names(entries: evicted), expected: ["older"]);
    }
    [Fact]
    public void AnEntryNoOneUsedSinceTheAgeBoundGoesWhateverRoomTheBoundHas() {
        var evicted = CacheRetention.SelectEvictions(
            bound: new CacheBound(MaxBytes: long.MaxValue, MaxEntries: int.MaxValue),
            entries: [Entry(name: "fresh", minutesAgo: 5), Entry(name: "stale", minutesAgo: 61), Entry(name: "used", minutesAgo: 120)],
            inUse: "/cache/used",
            usedSinceUtc: Now.AddMinutes(value: -60)
        );

        Assert.Equal(actual: Names(entries: evicted), expected: ["stale"]);
    }
    [Fact]
    public void AnEnforcementDeletesOnlyEntriesAndLeavesOneAReaderHolds() {
        using var cache = new TemporaryDirectory(prefix: "puck-cache-retention-law-");
        var written = DateTime.UtcNow;

        string Write(string name, int minutesAgo) {
            var path = cache.WriteText(name: name, text: name);

            File.SetLastWriteTimeUtc(lastWriteTimeUtc: written.AddMinutes(value: -minutesAgo), path: path);
            return path;
        }
        var newest = Write(minutesAgo: 1, name: "newest.bin");
        var held = Write(minutesAgo: 2, name: "held.bin");
        var oldest = Write(minutesAgo: 3, name: "nested/oldest.bin");
        var other = Write(minutesAgo: 60, name: "other.txt");

        CacheRetentionResult result;

        using (new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: held, share: FileShare.Read)) {
            result = CacheRetention.Enforce(
                bound: CacheBound.Entries(maxEntries: 1),
                directory: cache.RootPath,
                isEntry: static file => (file.Extension == ".bin"),
                search: SearchOption.AllDirectories
            );
        }

        Assert.True(condition: File.Exists(path: newest));
        Assert.False(condition: File.Exists(path: oldest));
        Assert.True(condition: File.Exists(path: other));

        // Windows refuses to delete a file a reader holds without sharing deletion; other platforms delete it.
        Assert.Equal(actual: File.Exists(path: held), expected: OperatingSystem.IsWindows());
        Assert.Equal(actual: result.Removed, expected: (OperatingSystem.IsWindows() ? 1 : 2));
        Assert.Equal(actual: (result.Removed + result.Kept), expected: 3);
    }
    // A cache of sets keeps and removes each subdirectory whole, its last use its newest entry's, and a loose entry as a
    // set of its own; the set in use stays and, alone past the bound, trims its own oldest entries; a set a reader holds
    // an entry of keeps what it cannot delete; a set with no entry is aged by its directory; a writer enforces once an
    // interval; and a missing directory holds nothing.
    [Fact]
    public void ACacheOfSetsRemovesASetWholeLeastRecentlyUsedFirst() {
        using var cache = new TemporaryDirectory(prefix: "puck-cache-retention-sets-law-");
        var written = DateTime.UtcNow;
        var bound = new CacheBound(MaxBytes: 3, MaxEntries: 10);

        string Write(string name, int minutesAgo) {
            var path = cache.WriteText(name: name, text: "x");

            File.SetLastWriteTimeUtc(lastWriteTimeUtc: written.AddMinutes(value: -minutesAgo), path: path);
            return path;
        }
        // The in-use set's entries are the oldest, but it stays; "fresh" was just used, so it stays whole; "stale", the
        // loose entry and the empty set, aged by its directory, are older and go whole.
        var used = Write(minutesAgo: 90, name: "used/a.bin");
        var fresh = (Write(minutesAgo: 80, name: "fresh/a.bin"), Write(minutesAgo: 1, name: "fresh/b.bin"));
        var stale = (Write(minutesAgo: 20, name: "stale/a.bin"), Write(minutesAgo: 10, name: "stale/b.bin"));
        var loose = Write(minutesAgo: 30, name: "loose.bin");
        var note = Write(minutesAgo: 5, name: "stale/note.txt");
        var empty = Directory.CreateDirectory(path: Path.Combine(path1: cache.RootPath, path2: "empty")).FullName;

        Directory.SetLastWriteTimeUtc(lastWriteTimeUtc: written.AddDays(value: -1), path: empty);

        var result = CacheRetention.EnforceSets(bound: bound, directory: cache.RootPath, inUse: used, isEntry: static file => (file.Extension == ".bin"));

        Assert.True(condition: File.Exists(path: used));
        Assert.True(condition: File.Exists(path: fresh.Item1) && File.Exists(path: fresh.Item2));
        Assert.False(condition: File.Exists(path: stale.Item1) || File.Exists(path: stale.Item2) || File.Exists(path: note) || File.Exists(path: loose));
        Assert.False(condition: Directory.Exists(path: Path.GetDirectoryName(path: stale.Item1)));
        Assert.False(condition: Directory.Exists(path: empty));
        Assert.Equal(expected: new CacheRetentionResult(Kept: 3, KeptBytes: 3, Removed: 3, RemovedBytes: 3), actual: result);

        // Alone past the bound, the set in use trims its own oldest entries and keeps the one in use.
        var second = Write(minutesAgo: 70, name: "used/b.bin");
        var third = Write(minutesAgo: 60, name: "used/c.bin");
        var trimmed = CacheRetention.EnforceSets(bound: new CacheBound(MaxBytes: 2, MaxEntries: 10), directory: cache.RootPath, inUse: used, isEntry: static file => (file.Extension == ".bin"));

        Assert.True(condition: File.Exists(path: used) && File.Exists(path: third));
        Assert.False(condition: File.Exists(path: second) || File.Exists(path: fresh.Item1) || File.Exists(path: fresh.Item2));
        Assert.Equal(expected: 3, actual: trimmed.Removed);

        // A set a reader holds an entry of keeps what it cannot delete, and its directory with it, on Windows.
        var held = Write(minutesAgo: 50, name: "held/a.bin");

        using (new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: held, share: FileShare.Read)) {
            _ = CacheRetention.EnforceSets(bound: new CacheBound(MaxBytes: 2, MaxEntries: 10), directory: cache.RootPath, inUse: used, isEntry: static file => (file.Extension == ".bin"));
        }
        Assert.Equal(actual: File.Exists(path: held), expected: OperatingSystem.IsWindows());

        // A writer enforces once an interval, and a missing directory holds nothing.
        var other = cache.PathOf(name: "other");

        Assert.True(condition: CacheRetention.EnforceSetsOnWrite(bound: bound, directory: other, isEntry: static _ => true));
        Assert.False(condition: CacheRetention.EnforceSetsOnWrite(bound: bound, directory: other, isEntry: static _ => true));
        Assert.Equal(expected: default, actual: CacheRetention.EnforceSets(bound: bound, directory: other, isEntry: static _ => true));
    }
    [Fact]
    public void AWriterListsADirectoryOnceAnInterval() {
        using var cache = new TemporaryDirectory(prefix: "puck-cache-retention-law-");
        var bound = CacheBound.Entries(maxEntries: 1);

        _ = cache.WriteText(name: "a.bin", text: "a");
        _ = cache.WriteText(name: "b.bin", text: "b");

        Assert.True(condition: CacheRetention.EnforceOnWrite(bound: bound, directory: cache.RootPath, isEntry: static _ => true));
        Assert.Single(collection: Directory.GetFiles(path: cache.RootPath));

        _ = cache.WriteText(name: "c.bin", text: "c");

        Assert.False(condition: CacheRetention.EnforceOnWrite(bound: bound, directory: cache.RootPath, isEntry: static _ => true));
        Assert.Equal(actual: Directory.GetFiles(path: cache.RootPath).Length, expected: 2);
    }
}
