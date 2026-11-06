using System.Collections.Concurrent;

namespace Puck.Abstractions;

/// <summary>How much one cache keeps: at most <paramref name="MaxEntries"/> entries holding at most
/// <paramref name="MaxBytes"/> bytes between them.</summary>
/// <param name="MaxEntries">The most entries kept, at least 1.</param>
/// <param name="MaxBytes">The most bytes the kept entries hold, at least 0; <see cref="long.MaxValue"/> for no byte
/// bound.</param>
public readonly record struct CacheBound(int MaxEntries, long MaxBytes) {
    /// <summary>A bound on the count of entries alone.</summary>
    /// <param name="maxEntries">The most entries kept.</param>
    /// <returns>The bound.</returns>
    public static CacheBound Entries(int maxEntries) => new(MaxBytes: long.MaxValue, MaxEntries: maxEntries);

    /// <summary>Gets the bound an enforcement on write trims to once the cache passes this one: an eighth below it in
    /// bytes and in entries (at least one entry), so the writes that follow fit the headroom before the next listing.</summary>
    public CacheBound LowWater => new(MaxBytes: (MaxBytes - (MaxBytes / 8)), MaxEntries: Math.Max(val1: 1, val2: (MaxEntries - (MaxEntries / 8))));
}
/// <summary>One entry a cache holds: a file or a directory, when it was last used, and how many bytes it holds.</summary>
/// <param name="Path">The entry's full path.</param>
/// <param name="LastUsedUtc">When it was last used, in UTC: its stamp (<see cref="CacheRetention.Stamp"/>).</param>
/// <param name="Bytes">The bytes it holds; 0 where the owner bounds entries alone.</param>
public readonly record struct CacheEntry(string Path, DateTime LastUsedUtc, long Bytes);
/// <summary>What one enforcement removed from a cache and what it kept.</summary>
/// <param name="Removed">The entries removed.</param>
/// <param name="RemovedBytes">Their total bytes.</param>
/// <param name="Kept">The entries kept: within the bound, in use, or held open by a reader.</param>
/// <param name="KeptBytes">Their total bytes.</param>
public readonly record struct CacheRetentionResult(int Removed, long RemovedBytes, int Kept, long KeptBytes);
/// <summary>
/// The one retention policy of Puck's on-disk caches: least recently used out, under a <see cref="CacheBound"/>.
/// <para>
/// An entry's last use is its stamp: the last write time of the file or directory that stands for it. An owner stamps an
/// entry when it reads it (<see cref="Stamp"/>), and writing one stamps it. When an owner writes, it enforces its bound
/// (<see cref="EnforceOnWrite"/>; <see cref="EnforceSetsOnWrite"/> where entries come in sets one reader uses together, a
/// subdirectory a set kept or removed whole; or <see cref="SelectEvictions"/> where it removes entries its own way): entries are
/// ordered most recently used first, equal stamps falling to the ordinally lesser path, the entry in use is kept
/// whatever its stamp and counts against the bound first, and the rest are kept while both the count and the bytes stay
/// within the bound. From the first entry that does not fit, every older one goes. An entry that cannot be removed
/// because a reader holds it open is in use, and stays.
/// </para>
/// <para>
/// An enforcement on write lists the directory only when the bound may have been passed: each process keeps a tally per
/// directory of what its last listing kept plus what it has written since, and lists again once that tally passes the
/// bound or <see cref="EnforceInterval"/> has gone by. A listing over the bound trims to its
/// <see cref="CacheBound.LowWater"/>, so a full cache lists once per eighth of its bound written rather than on every
/// write. One process's writes therefore never leave the cache over its bound. Writers in other processes are counted
/// only by the next listing: while several processes write one directory at once, the cache can pass its bound by what
/// the others wrote since this process last listed, beyond the headroom, until one of them lists again.
/// </para>
/// <para>
/// A stamp is file-system metadata and a cache's contents are content-addressed, so retention never decides what a
/// cache answers: an evicted entry costs its next reader a derivation, never a different result.
/// </para>
/// </summary>
public static class CacheRetention {
    /// <summary>The longest a writing process goes without listing a directory it writes, so that it counts other
    /// processes' writes; within it, it lists only when its own tally passes the bound.</summary>
    public static readonly TimeSpan EnforceInterval = TimeSpan.FromMinutes(minutes: 1);

    private static readonly ConcurrentDictionary<string, Tally> Tallies = new(comparer: StringComparer.Ordinal);

    /// <summary>Records that <paramref name="path"/> was used now. A stamp that cannot be written costs at most an
    /// early eviction, so a missing or refusing entry is left as it is.</summary>
    /// <param name="path">The file or directory standing for the entry.</param>
    public static void Stamp(string path) {
        try {
            if (Directory.Exists(path: path)) {
                Directory.SetLastWriteTimeUtc(lastWriteTimeUtc: DateTime.UtcNow, path: path);
            } else {
                File.SetLastWriteTimeUtc(lastWriteTimeUtc: DateTime.UtcNow, path: path);
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // Gone, or held by a writer that refuses attribute changes: the entry keeps its older stamp.
        }
    }
    /// <summary>Chooses the entries a cache removes to stay within <paramref name="bound"/>.</summary>
    /// <param name="entries">Every entry the cache holds.</param>
    /// <param name="bound">The bound.</param>
    /// <param name="inUse">The entry the owner is using, kept whatever its stamp; or <see langword="null"/>.</param>
    /// <param name="usedSinceUtc">The earliest stamp an entry keeps, in UTC, whatever room the bound has; or
    /// <see langword="null"/> for no age bound.</param>
    /// <returns>The entries to remove, most recently used first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The bound keeps no entry or a negative number of bytes.</exception>
    public static IReadOnlyList<CacheEntry> SelectEvictions(IEnumerable<CacheEntry> entries, CacheBound bound, string? inUse = null, DateTime? usedSinceUtc = null) {
        ArgumentNullException.ThrowIfNull(argument: entries);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: bound.MaxEntries, other: 1, paramName: nameof(bound));
        ArgumentOutOfRangeException.ThrowIfNegative(value: bound.MaxBytes, paramName: nameof(bound));

        var ordered = entries.ToList();

        ordered.Sort(comparison: (left, right) => {
            var leftInUse = IsInUse(entry: left, inUse: inUse);

            if (leftInUse != IsInUse(entry: right, inUse: inUse)) {
                return (leftInUse ? -1 : 1);
            }

            var order = right.LastUsedUtc.CompareTo(value: left.LastUsedUtc);

            return ((order != 0) ? order : string.CompareOrdinal(strA: left.Path, strB: right.Path));
        });

        var evictions = new List<CacheEntry>();
        var count = 0;
        var bytes = 0L;
        var full = false;

        foreach (var entry in ordered) {
            if (IsInUse(entry: entry, inUse: inUse)) {
                count++;
                bytes += entry.Bytes;
            } else if (!full && (count < bound.MaxEntries) && (entry.Bytes <= (bound.MaxBytes - bytes)) && ((usedSinceUtc is not { } since) || (entry.LastUsedUtc >= since))) {
                count++;
                bytes += entry.Bytes;
            } else {
                full = true;
                evictions.Add(item: entry);
            }
        }

        return evictions;
    }
    /// <summary>Holds a directory of file entries to <paramref name="bound"/>: lists the files <paramref name="isEntry"/>
    /// accepts, chooses the evictions (<see cref="SelectEvictions"/>) and deletes them. A file that cannot be deleted is
    /// kept.</summary>
    /// <param name="directory">The cache directory; one that does not exist holds nothing.</param>
    /// <param name="bound">The bound.</param>
    /// <param name="isEntry">Which files are entries; anything else is left alone.</param>
    /// <param name="search">Whether entries lie in the directory alone or in its subdirectories too.</param>
    /// <param name="inUse">The entry the owner is using, or <see langword="null"/>.</param>
    /// <param name="usedSinceUtc">The earliest stamp an entry keeps, in UTC; or <see langword="null"/> for no age
    /// bound.</param>
    /// <returns>What was removed and kept.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="isEntry"/> is <see langword="null"/>.</exception>
    public static CacheRetentionResult Enforce(string directory, CacheBound bound, Func<FileInfo, bool> isEntry, SearchOption search = SearchOption.TopDirectoryOnly, string? inUse = null, DateTime? usedSinceUtc = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);
        ArgumentNullException.ThrowIfNull(argument: isEntry);

        return EnforceFiles(bound: bound, directory: directory, inUse: inUse, isEntry: isEntry, search: search, trimTo: bound, usedSinceUtc: usedSinceUtc).Result;
    }
    /// <summary>Holds a directory of file entries to <paramref name="bound"/> after a write (<see cref="Enforce"/>),
    /// listing it only when this process's tally of it may have passed the bound or <see cref="EnforceInterval"/> has
    /// gone by since its last listing, and trimming a cache over its bound to <see cref="CacheBound.LowWater"/>: what an
    /// owner calls after each write.</summary>
    /// <param name="directory">The cache directory.</param>
    /// <param name="bound">The bound.</param>
    /// <param name="isEntry">Which files are entries.</param>
    /// <param name="search">Whether entries lie in subdirectories too.</param>
    /// <param name="inUse">The entry the owner just wrote, counted into the tally, or <see langword="null"/>.</param>
    /// <returns>Whether the directory was listed.</returns>
    public static bool EnforceOnWrite(string directory, CacheBound bound, Func<FileInfo, bool> isEntry, SearchOption search = SearchOption.TopDirectoryOnly, string? inUse = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);
        ArgumentNullException.ThrowIfNull(argument: isEntry);

        var key = Path.GetFullPath(path: directory);

        return OnWrite(bound: bound, key: key, newUnit: true, written: inUse, enforce: () => EnforceFiles(bound: bound, directory: key, inUse: inUse, isEntry: isEntry, search: search, trimTo: bound.LowWater, usedSinceUtc: null));
    }
    /// <summary>Holds a directory whose entries come in sets to <paramref name="bound"/>, a set at a time: each
    /// subdirectory is one set, its entries the files <paramref name="isEntry"/> accepts anywhere beneath it, its bytes
    /// theirs and its last use its most recently used entry's stamp; a file directly in the directory that
    /// <paramref name="isEntry"/> accepts is a set of its own. The sets are kept and removed as
    /// <see cref="SelectEvictions"/> chooses, the set in use counted first, so a set goes whole, least recently used
    /// first, and a set one entry of which is in use stays whole. When the set in use alone holds more than the bound, its
    /// own entries go least recently used first, the entry in use kept, until it fits. A file that cannot be deleted
    /// because a reader holds it is kept, and its set with it.</summary>
    /// <param name="directory">The cache directory; one that does not exist holds nothing.</param>
    /// <param name="bound">The bound: its count bounds the sets, its bytes their entries' bytes.</param>
    /// <param name="isEntry">Which files are entries; anything else is left alone.</param>
    /// <param name="inUse">The entry the owner just wrote or is using, whose set is the set in use; or
    /// <see langword="null"/>.</param>
    /// <returns>What was removed and kept, counted in entries.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="isEntry"/> is <see langword="null"/>.</exception>
    public static CacheRetentionResult EnforceSets(string directory, CacheBound bound, Func<FileInfo, bool> isEntry, string? inUse = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);
        ArgumentNullException.ThrowIfNull(argument: isEntry);

        return EnforceSetsCore(bound: bound, directory: directory, inUse: inUse, isEntry: isEntry, trimTo: bound).Result;
    }
    /// <summary>Holds a directory of sets to <paramref name="bound"/> after a write (<see cref="EnforceSets"/>), listing
    /// it only when this process's tally of it may have passed the bound or <see cref="EnforceInterval"/> has gone by
    /// since its last listing, and trimming a cache over its bound to <see cref="CacheBound.LowWater"/> a set at a time:
    /// what an owner whose entries come in sets calls after each write.</summary>
    /// <param name="directory">The cache directory.</param>
    /// <param name="bound">The bound.</param>
    /// <param name="isEntry">Which files are entries.</param>
    /// <param name="inUse">The entry the owner just wrote, counted into the tally, or <see langword="null"/>.</param>
    /// <returns>Whether the directory was listed.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    public static bool EnforceSetsOnWrite(string directory, CacheBound bound, Func<FileInfo, bool> isEntry, string? inUse = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);
        ArgumentNullException.ThrowIfNull(argument: isEntry);

        var key = Path.GetFullPath(path: directory);

        return OnWrite(bound: bound, key: key, newUnit: false, written: inUse, enforce: () => EnforceSetsCore(bound: bound, directory: key, inUse: inUse, isEntry: isEntry, trimTo: bound.LowWater));
    }

    // Counts a write into the directory's tally and lists the directory when the tally may have passed the bound, the
    // interval has gone by, or this process never listed it; the listing resets the tally to what it kept. A written
    // unit is a new entry (flat) or, for sets, a set the last listing did not hold. Holding the tally's lock across the
    // listing keeps one process's writers from listing twice for one overflow; an overestimate (a rewritten entry
    // counted again) only lists earlier.
    private static bool OnWrite(string key, CacheBound bound, string? written, bool newUnit, Func<Listing> enforce) {
        var tally = Tallies.GetOrAdd(key: key, valueFactory: static _ => new Tally());
        var bytes = 0L;
        var path = ((written is null) ? null : Path.GetFullPath(path: written));

        if (path is not null) {
            try {
                bytes = new FileInfo(fileName: path).Length;
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                // Gone already: it adds nothing.
            }
        }

        lock (tally) {
            var now = Environment.TickCount64;

            tally.Bytes += bytes;
            if (path is not null) {
                if (newUnit) {
                    tally.Units++;
                } else if (tally.Sets is { } sets && sets.Add(item: SetOf(root: key, path: path))) {
                    tally.Units++;
                }
            }
            if (tally.Listed && (tally.Bytes <= bound.MaxBytes) && (tally.Units <= bound.MaxEntries) && ((now - tally.ListedAt) < ((long)EnforceInterval.TotalMilliseconds))) {
                return false;
            }

            var listing = enforce();

            tally.Bytes = listing.Result.KeptBytes;
            tally.Units = listing.KeptUnits;
            tally.Sets = listing.KeptSets;
            tally.ListedAt = Environment.TickCount64;
            tally.Listed = true;

            return true;
        }
    }
    // Lists a flat directory, then removes entries down to trimTo when the listing is over bound (or the age bound
    // applies). When bound and trimTo are one bound this is the ordinary enforcement.
    private static Listing EnforceFiles(string directory, CacheBound bound, CacheBound trimTo, Func<FileInfo, bool> isEntry, SearchOption search, string? inUse, DateTime? usedSinceUtc) {
        var entries = new List<CacheEntry>();

        try {
            foreach (var file in new DirectoryInfo(path: directory).EnumerateFiles(searchOption: search, searchPattern: "*")) {
                if (isEntry(arg: file)) {
                    entries.Add(item: new CacheEntry(Bytes: file.Length, LastUsedUtc: file.LastWriteTimeUtc, Path: file.FullName));
                }
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // A directory that is missing or cannot be listed holds nothing this enforcement can remove.
        }

        var total = entries.Sum(selector: static entry => entry.Bytes);
        var removed = 0;
        var removedBytes = 0L;

        if ((usedSinceUtc is not null) || (entries.Count > bound.MaxEntries) || (total > bound.MaxBytes)) {
            foreach (var eviction in SelectEvictions(bound: trimTo, entries: entries, inUse: ((inUse is null) ? null : Path.GetFullPath(path: inUse)), usedSinceUtc: usedSinceUtc)) {
                if (TryDelete(path: eviction.Path)) {
                    removed++;
                    removedBytes += eviction.Bytes;
                }
            }
        }

        var result = new CacheRetentionResult(Kept: (entries.Count - removed), KeptBytes: (total - removedBytes), Removed: removed, RemovedBytes: removedBytes);

        return new Listing(KeptSets: null, KeptUnits: result.Kept, Result: result);
    }
    // Lists a directory of sets, then removes sets (and, when the set in use alone is over, that set's own entries) down
    // to trimTo when the listing is over bound. When bound and trimTo are one bound this is the ordinary enforcement.
    private static Listing EnforceSetsCore(string directory, CacheBound bound, CacheBound trimTo, Func<FileInfo, bool> isEntry, string? inUse) {
        var root = Path.GetFullPath(path: directory);
        var used = ((inUse is null) ? null : Path.GetFullPath(path: inUse));
        var sets = new List<(CacheEntry Set, List<CacheEntry> Entries)>();

        try {
            var info = new DirectoryInfo(path: root);

            foreach (var file in info.EnumerateFiles(searchOption: SearchOption.TopDirectoryOnly, searchPattern: "*")) {
                if (isEntry(arg: file)) {
                    var entry = new CacheEntry(Bytes: file.Length, LastUsedUtc: file.LastWriteTimeUtc, Path: file.FullName);

                    sets.Add(item: (entry, [entry]));
                }
            }
            foreach (var child in info.EnumerateDirectories(searchOption: SearchOption.TopDirectoryOnly, searchPattern: "*")) {
                var entries = child.EnumerateFiles(searchOption: SearchOption.AllDirectories, searchPattern: "*")
                    .Where(predicate: isEntry)
                    .Select(selector: static file => new CacheEntry(Bytes: file.Length, LastUsedUtc: file.LastWriteTimeUtc, Path: file.FullName))
                    .ToList();
                var lastUsed = ((entries.Count == 0) ? child.LastWriteTimeUtc : entries.Max(selector: static entry => entry.LastUsedUtc));

                sets.Add(item: (new CacheEntry(Bytes: entries.Sum(selector: static entry => entry.Bytes), LastUsedUtc: lastUsed, Path: child.FullName), entries));
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // A directory that is missing or cannot be listed holds nothing this enforcement can remove.
        }

        var removed = 0;
        var removedBytes = 0L;
        var gone = new HashSet<string>(comparer: PuckPaths.Comparer);
        var totalBytes = sets.Sum(selector: static set => set.Set.Bytes);

        if ((sets.Count > bound.MaxEntries) || (totalBytes > bound.MaxBytes)) {
            var inUseSet = ((used is null) ? -1 : sets.FindIndex(match: set => set.Entries.Any(predicate: entry => IsInUse(entry: entry, inUse: used))));

            // The set in use trims itself first when it alone holds more than the bound.
            if ((inUseSet >= 0) && (sets[inUseSet].Set.Bytes > trimTo.MaxBytes)) {
                var (set, entries) = sets[inUseSet];
                var kept = set.Bytes;

                foreach (var eviction in SelectEvictions(bound: new CacheBound(MaxBytes: trimTo.MaxBytes, MaxEntries: int.MaxValue), entries: entries, inUse: used)) {
                    if (TryDelete(path: eviction.Path)) {
                        removed++;
                        removedBytes += eviction.Bytes;
                        kept -= eviction.Bytes;
                    }
                }

                sets[inUseSet] = ((set with { Bytes = kept }), entries);
            }

            foreach (var eviction in SelectEvictions(bound: trimTo, entries: sets.Select(selector: static set => set.Set), inUse: ((inUseSet >= 0) ? sets[inUseSet].Set.Path : null))) {
                var whole = true;

                foreach (var entry in sets.First(predicate: set => string.Equals(a: set.Set.Path, b: eviction.Path, comparisonType: PuckPaths.Comparison)).Entries) {
                    if (TryDelete(path: entry.Path)) {
                        removed++;
                        removedBytes += entry.Bytes;
                    } else {
                        whole = false;
                    }
                }
                if (Directory.Exists(path: eviction.Path)) {
                    try {
                        Directory.Delete(path: eviction.Path, recursive: true);
                    } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                        // A file a reader holds keeps what is left of the set until a later enforcement.
                        whole = false;
                    }
                }
                if (whole) {
                    _ = gone.Add(item: eviction.Path);
                }
            }
        }

        var total = sets.Sum(selector: static set => set.Entries.Count);
        var keptSets = new HashSet<string>(collection: sets.Select(selector: static set => set.Set.Path).Where(predicate: path => !gone.Contains(item: path)), comparer: PuckPaths.Comparer);
        var result = new CacheRetentionResult(
            Kept: (total - removed),
            KeptBytes: (sets.Sum(selector: static set => set.Entries.Sum(selector: static entry => entry.Bytes)) - removedBytes),
            Removed: removed,
            RemovedBytes: removedBytes
        );

        return new Listing(KeptSets: keptSets, KeptUnits: keptSets.Count, Result: result);
    }
    // The set a written path belongs to: the top-level subdirectory of root holding it, or the file itself directly in root.
    private static string SetOf(string root, string path) {
        var relative = Path.GetRelativePath(path: path, relativeTo: root);
        var separator = relative.IndexOfAny(anyOf: [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

        return ((separator < 0) ? path : Path.Combine(path1: root, path2: relative[..separator]));
    }
    // Deletes a file, answering whether it went: one a reader holds open is in use, and stays.
    private static bool TryDelete(string path) {
        try {
            File.Delete(path: path);

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return false;
        }
    }

    private static bool IsInUse(CacheEntry entry, string? inUse) =>
        ((inUse is not null) && string.Equals(a: entry.Path, b: inUse, comparisonType: PuckPaths.Comparison));

    // What one listing kept: the result, the units it counts against the bound's entries (entries, or sets), and for a
    // directory of sets the sets it kept, so a later write can tell a new set from a kept one.
    private readonly record struct Listing(CacheRetentionResult Result, long KeptUnits, HashSet<string>? KeptSets);
    // A process's tally of one directory since its last listing.
    private sealed class Tally {
        public long Bytes;
        public bool Listed;
        public long ListedAt;
        public HashSet<string>? Sets;
        public long Units;
    }
}
