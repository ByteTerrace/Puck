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
