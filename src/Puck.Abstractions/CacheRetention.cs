using System.Collections.Concurrent;
using Puck.Confinement;

namespace Puck.Abstractions;

/// <summary>How much one cache keeps: at most <paramref name="MaxEntries"/> entries holding at most
/// <paramref name="MaxBytes"/> bytes between them, the <see cref="MinEntries"/> most recently used of them whatever
/// their bytes.</summary>
/// <param name="MaxEntries">The most entries kept, at least 1.</param>
/// <param name="MaxBytes">The most bytes the kept entries hold, at least 0; <see cref="long.MaxValue"/> for no byte
/// bound.</param>
public readonly record struct CacheBound(int MaxEntries, long MaxBytes) {
    /// <summary>A bound on the count of entries alone.</summary>
    /// <param name="maxEntries">The most entries kept.</param>
    /// <returns>The bound.</returns>
    public static CacheBound Entries(int maxEntries) => new(MaxBytes: long.MaxValue, MaxEntries: maxEntries);

    /// <summary>Gets the entries the cache keeps whatever their bytes: the most recently used, those in use and pinned
    /// counted first. <see cref="MaxBytes"/> bounds only the entries past them, so a cache whose entries are large beside
    /// its byte bound still keeps what its readers used last; zero, the default, for a cache its bytes bound
    /// alone.</summary>
    public int MinEntries { get; init; }
    /// <summary>Gets the bound an enforcement on write trims to once the cache passes this one: an eighth below it in
    /// bytes and in entries (at least one entry), so the writes that follow fit the headroom before the next listing.</summary>
    public CacheBound LowWater => (this with { MaxBytes = (MaxBytes - (MaxBytes / 8)), MaxEntries = Math.Max(val1: 1, val2: (MaxEntries - (MaxEntries / 8))) });
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
/// within the bound, the bound's <see cref="CacheBound.MinEntries"/> most recent whatever their bytes. From the first
/// entry that does not fit, every older one goes. An entry that cannot be removed because a reader holds it open is in
/// use, and stays.
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
/// <para>
/// A set a running process depends on is pinned by its hold (<see cref="HoldSet"/>): a shared lock on the lease file
/// beside the set's directory, which the operating system releases with the process however it ends. No enforcement, in
/// any process, removes a pinned set or trims its entries; it counts against the bound first, as the set in use does.
/// </para>
/// </summary>
public static class CacheRetention {
    /// <summary>The longest a writing process goes without listing a directory it writes, so that it counts other
    /// processes' writes; within it, it lists only when its own tally passes the bound.</summary>
    public static readonly TimeSpan EnforceInterval = TimeSpan.FromMinutes(minutes: 1);
    /// <summary>How long a temporary file an <see cref="AtomicFile"/> write staged must go unwritten before a listing of
    /// its directory considers removing it. Age only chooses candidates: a listing removes a temporary only when
    /// <see cref="AtomicFile.IsAbandoned"/> finds its writer is not running, so a live writer's temporary stays however
    /// long it waits between staging and publication.</summary>
    public static readonly TimeSpan AbandonedTemporaryAge = TimeSpan.FromMinutes(minutes: 1);

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

        return SelectEvictionsCore(bound: bound, entries: entries, kept: entry => IsInUse(entry: entry, inUse: inUse), usedSinceUtc: usedSinceUtc);
    }
    /// <summary>Pins a set against every enforcement (<see cref="EnforceSets"/>, <see cref="EnforceSetsOnWrite"/>) while the
    /// returned hold stays open: a shared lock on <c>&lt;set&gt;.lease</c> beside the set's directory, which any number
    /// of processes may hold together. The set need not exist yet.</summary>
    /// <param name="setDirectory">The set's directory: a subdirectory of the cache directory.</param>
    /// <returns>The hold, released when disposed or when its process ends; or <see langword="null"/> when the lease file
    /// cannot be opened, so the set is not pinned.</returns>
    /// <exception cref="ArgumentException"><paramref name="setDirectory"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    public static FileStream? HoldSet(string setDirectory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: setDirectory);

        try {
            return FileLock.TryAcquireShared(path: LeaseOf(set: Path.GetFullPath(path: setDirectory)));
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        }
    }

    // The lease a set's holders lock shared, beside the set's directory, so it is never one of the set's entries.
    private static string LeaseOf(string set) => (Path.TrimEndingDirectorySeparator(path: set) + ".lease");
    // Whether a process holds a set's lease: an exclusive open of an existing lease fails while any holder has it.
    private static bool IsPinned(string set) {
        var lease = LeaseOf(set: set);

        if (!File.Exists(path: lease)) { return false; }
        try {
            using var probe = FileLock.TryAcquire(path: lease);

            return (probe is null);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return false;
        }
    }
    // Deletes an evicted set's lease while no holder can take it.
    private static void DeleteLease(string set) {
        try {
            using var deleting = FileLock.TryAcquireForDeletion(path: LeaseOf(set: set));
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // Left for a later eviction.
        }
    }
    private static List<CacheEntry> SelectEvictionsCore(IEnumerable<CacheEntry> entries, CacheBound bound, Func<CacheEntry, bool> kept, DateTime? usedSinceUtc) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: bound.MaxEntries, other: 1, paramName: nameof(bound));
        ArgumentOutOfRangeException.ThrowIfNegative(value: bound.MaxBytes, paramName: nameof(bound));

        var ordered = entries.ToList();

        ordered.Sort(comparison: (left, right) => {
            var leftInUse = kept(arg: left);

            if (leftInUse != kept(arg: right)) {
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
            if (kept(arg: entry)) {
                count++;
                bytes += entry.Bytes;
            } else if (!full && (count < bound.MaxEntries) && ((count < bound.MinEntries) || (entry.Bytes <= (bound.MaxBytes - bytes))) && ((usedSinceUtc is not { } since) || (entry.LastUsedUtc >= since))) {
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
    /// <param name="isEntry">Which files are entries, judged by file name; anything else is left alone. A temporary
    /// (<see cref="AtomicFile.IsTemporary"/>) is never offered: it is a write in progress, or one
    /// <see cref="AbandonedTemporaryAge"/> governs.</param>
    /// <param name="search">Whether entries lie in the directory alone or in its subdirectories too.</param>
    /// <param name="inUse">The entry the owner is using, or <see langword="null"/>.</param>
    /// <param name="usedSinceUtc">The earliest stamp an entry keeps, in UTC; or <see langword="null"/> for no age
    /// bound.</param>
    /// <returns>What was removed and kept.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="isEntry"/> is <see langword="null"/>.</exception>
    public static CacheRetentionResult Enforce(string directory, CacheBound bound, Func<string, bool> isEntry, SearchOption search = SearchOption.TopDirectoryOnly, string? inUse = null, DateTime? usedSinceUtc = null) {
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
    /// <param name="isEntry">Which files are entries, judged by file name; never offered a temporary.</param>
    /// <param name="search">Whether entries lie in subdirectories too.</param>
    /// <param name="inUse">The entry the owner just wrote, counted into the tally, or <see langword="null"/>.</param>
    /// <returns>Whether the directory was listed.</returns>
    public static bool EnforceOnWrite(string directory, CacheBound bound, Func<string, bool> isEntry, SearchOption search = SearchOption.TopDirectoryOnly, string? inUse = null) {
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
    /// first, and a set one entry of which is in use stays whole. When the set in use alone holds more than a bound that
    /// keeps no set whatever its bytes (<see cref="CacheBound.MinEntries"/> of zero), its own entries go least recently
    /// used first, the entry in use kept, until it fits. A file that cannot be deleted
    /// because a reader holds it is kept, and its set with it.</summary>
    /// <param name="directory">The cache directory; one that does not exist holds nothing.</param>
    /// <param name="bound">The bound: its count bounds the sets, its bytes their entries' bytes.</param>
    /// <param name="isEntry">Which files are entries, judged by file name; anything else is left alone. A temporary
    /// (<see cref="AtomicFile.IsTemporary"/>) is never offered: it is a write in progress, or one
    /// <see cref="AbandonedTemporaryAge"/> governs.</param>
    /// <param name="inUse">The entry the owner just wrote or is using, whose set is the set in use; or
    /// <see langword="null"/>.</param>
    /// <returns>What was removed and kept, counted in entries.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="isEntry"/> is <see langword="null"/>.</exception>
    public static CacheRetentionResult EnforceSets(string directory, CacheBound bound, Func<string, bool> isEntry, string? inUse = null) {
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
    /// <param name="isEntry">Which files are entries, judged by file name; never offered a temporary.</param>
    /// <param name="inUse">The entry the owner just wrote, counted into the tally, or <see langword="null"/>.</param>
    /// <param name="relieve">The bytes the cache is to give back whatever its bound has room for, as when the volume it
    /// lies on runs short: a positive value lists the directory now and trims it to that many bytes under what the
    /// listing finds, where that is below the bound's low water. The sets in use, pinned and within
    /// <see cref="CacheBound.MinEntries"/> stay, so the cache can give back less than asked.</param>
    /// <returns>Whether the directory was listed.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    public static bool EnforceSetsOnWrite(string directory, CacheBound bound, Func<string, bool> isEntry, string? inUse = null, long relieve = 0L) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);
        ArgumentNullException.ThrowIfNull(argument: isEntry);

        var key = Path.GetFullPath(path: directory);

        return OnWrite(bound: bound, force: (relieve > 0L), key: key, newUnit: false, written: inUse, enforce: () => EnforceSetsCore(bound: bound, directory: key, inUse: inUse, isEntry: isEntry, relieve: relieve, trimTo: bound.LowWater));
    }

    // Counts a write into the directory's tally and lists the directory when the tally may have passed the bound, the
    // interval has gone by, this process never listed it, or the caller forces it; the listing resets the tally to what
    // it kept. A written unit is a new entry (flat) or, for sets, a set the last listing did not hold. Holding the
    // tally's lock across the listing keeps one process's writers from listing twice for one overflow; an overestimate
    // (a rewritten entry counted again) only lists earlier. A listing can keep more than the bound (its entries in use,
    // pinned or within CacheBound.MinEntries), which listing again cannot trim, so the tally then lists once it has
    // grown by the bound's trim margin over what the listing kept, never on every write.
    private static bool OnWrite(string key, CacheBound bound, string? written, bool newUnit, Func<Listing> enforce, bool force = false) {
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
                } else if ((tally.Sets is { } sets) && sets.Add(item: SetOf(path: path, root: key))) {
                    tally.Units++;
                }
            }
            if (!force && tally.Listed && (tally.Bytes <= Math.Max(val1: bound.MaxBytes, val2: tally.ListAboveBytes)) && (tally.Units <= Math.Max(val1: bound.MaxEntries, val2: tally.ListAboveUnits)) && ((now - tally.ListedAt) < ((long)EnforceInterval.TotalMilliseconds))) {
                return false;
            }

            var listing = enforce();
            var low = bound.LowWater;

            tally.Bytes = listing.Result.KeptBytes;
            tally.Units = listing.KeptUnits;
            tally.Sets = listing.KeptSets;
            tally.ListAboveBytes = (tally.Bytes + (bound.MaxBytes - low.MaxBytes));
            tally.ListAboveUnits = (tally.Units + (bound.MaxEntries - low.MaxEntries));
            tally.ListedAt = Environment.TickCount64;
            tally.Listed = true;

            return true;
        }
    }
    // Lists a flat directory, then removes entries down to trimTo when the listing is over bound (or the age bound
    // applies). When bound and trimTo are one bound this is the ordinary enforcement.
    private static Listing EnforceFiles(string directory, CacheBound bound, CacheBound trimTo, Func<string, bool> isEntry, SearchOption search, string? inUse, DateTime? usedSinceUtc) {
        var root = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: directory));
        using var held = TryOpen(directory: root);
        var entries = new List<CacheEntry>();

        if (held is null) { return default; }
        try {
            foreach (var item in ListFiles(directory: held, recursive: (search == SearchOption.AllDirectories))) {
                if (isEntry(arg: item.Entry.Name)) {
                    entries.Add(item: new CacheEntry(Bytes: item.Entry.Length, LastUsedUtc: item.Entry.LastWriteTimeUtc, Path: PathOf(relativePath: item.Path, root: root)));
                }
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // A directory that cannot be listed holds nothing this enforcement can remove.
        }

        var total = entries.Sum(selector: static entry => entry.Bytes);
        var removed = 0;
        var removedBytes = 0L;

        if ((usedSinceUtc is not null) || (entries.Count > bound.MaxEntries) || (total > bound.MaxBytes)) {
            foreach (var eviction in SelectEvictions(bound: trimTo, entries: entries, inUse: ((inUse is null) ? null : Path.GetFullPath(path: inUse)), usedSinceUtc: usedSinceUtc)) {
                if (TryDelete(directory: held, relativePath: Path.GetRelativePath(path: eviction.Path, relativeTo: root))) {
                    removed++;
                    removedBytes += eviction.Bytes;
                }
            }
        }

        var result = new CacheRetentionResult(Kept: (entries.Count - removed), KeptBytes: (total - removedBytes), Removed: removed, RemovedBytes: removedBytes);

        return new Listing(KeptSets: null, KeptUnits: result.Kept, Result: result);
    }
    // Lists a directory of sets, then removes sets (and, when the set in use alone is over a bound that keeps no set
    // whatever its bytes, that set's own entries) down to trimTo when the listing is over bound, or down to `relieve`
    // bytes under the listing where that is lower. When bound and trimTo are one bound this is the ordinary enforcement.
    private static Listing EnforceSetsCore(string directory, CacheBound bound, CacheBound trimTo, Func<string, bool> isEntry, string? inUse, long relieve = 0L) {
        var root = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: directory));
        var used = ((inUse is null) ? null : Path.GetFullPath(path: inUse));
        // The set the written entry falls in, which alone keeps its entries' paths from the listing: a store of sets holds
        // tens of thousands of entries, listed at least once a minute while it is written, and every other set is
        // judged by its total and last use, its entries spelled again only if it is evicted (EntriesOf).
        var usedSet = ((used is null) ? null : SetOf(path: used, root: root));
        var sets = new List<SetListing>();
        using var held = TryOpen(directory: root);

        if (held is null) { return new Listing(KeptSets: new HashSet<string>(comparer: PuckPaths.Comparer), KeptUnits: 0, Result: default); }
        try {
            foreach (var entry in held.Entries()) {
                try {
                    var path = PathOf(relativePath: entry.Name, root: root);

                    if (entry.Kind == ConfinedEntryKind.Directory) {
                        using var child = held.Child(name: entry.Name);

                        if (child is null) { continue; }
                        var entries = (string.Equals(a: path, b: usedSet, comparisonType: PuckPaths.Comparison) ? new List<CacheEntry>() : null);
                        var bytes = 0L;
                        var count = 0;
                        var lastUsed = DateTime.MinValue;

                        foreach (var item in ListFiles(directory: child, recursive: true)) {
                            if (isEntry(arg: item.Entry.Name)) {
                                bytes += item.Entry.Length;
                                count++;
                                if (item.Entry.LastWriteTimeUtc > lastUsed) { lastUsed = item.Entry.LastWriteTimeUtc; }
                                entries?.Add(item: new CacheEntry(Bytes: item.Entry.Length, LastUsedUtc: item.Entry.LastWriteTimeUtc, Path: PathOf(relativePath: $"{entry.Name}/{item.Path}", root: root)));
                            }
                        }
                        sets.Add(item: new SetListing(Count: count, Entries: entries, IsDirectory: true, ListedBytes: bytes, Set: new CacheEntry(Bytes: bytes, LastUsedUtc: ((count == 0) ? entry.LastWriteTimeUtc : lastUsed), Path: path)));
                    } else if ((entry.Kind == ConfinedEntryKind.File) && !Abandon(directory: held, entry: entry) && isEntry(arg: entry.Name)) {
                        var file = new CacheEntry(Bytes: entry.Length, LastUsedUtc: entry.LastWriteTimeUtc, Path: path);

                        sets.Add(item: new SetListing(Count: 1, Entries: [file], IsDirectory: false, ListedBytes: file.Bytes, Set: file));
                    }
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                    // One unavailable child must not prevent counting the remaining sets.
                }
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // A directory that cannot be listed holds nothing this enforcement can remove.
        }

        var removed = 0;
        var removedBytes = 0L;
        var gone = new HashSet<string>(comparer: PuckPaths.Comparer);
        var totalBytes = sets.Sum(selector: static set => set.Set.Bytes);

        if (relieve > 0L) { trimTo = (trimTo with { MaxBytes = Math.Min(val1: trimTo.MaxBytes, val2: Math.Max(val1: 0L, val2: (totalBytes - relieve))) }); }
        if ((relieve > 0L) || (sets.Count > bound.MaxEntries) || (totalBytes > bound.MaxBytes)) {
            var inUseSet = ((used is null) ? -1 : sets.FindIndex(match: set => (set.Entries?.Any(predicate: entry => IsInUse(entry: entry, inUse: used)) ?? false)));
            // Sets a running process holds (HoldSet) are kept whole whatever their stamps.
            var pinned = new HashSet<string>(collection: sets.Where(predicate: static set => (set.IsDirectory && IsPinned(set: set.Set.Path))).Select(selector: static set => set.Set.Path), comparer: PuckPaths.Comparer);
            var inUsePath = ((inUseSet >= 0) ? sets[inUseSet].Set.Path : null);

            // The set in use trims itself first when it alone holds more than the bound, unless a process pins it or the
            // bound keeps its most recent sets whole (the set in use is the first of them).
            if ((inUseSet >= 0) && (trimTo.MinEntries == 0) && !pinned.Contains(item: sets[inUseSet].Set.Path) && (sets[inUseSet].Set.Bytes > trimTo.MaxBytes)) {
                var listed = sets[inUseSet];
                var kept = listed.Set.Bytes;

                foreach (var eviction in SelectEvictions(bound: new CacheBound(MaxBytes: trimTo.MaxBytes, MaxEntries: int.MaxValue), entries: listed.Entries!, inUse: used)) {
                    if (TryDelete(directory: held, relativePath: Path.GetRelativePath(path: eviction.Path, relativeTo: root))) {
                        removed++;
                        removedBytes += eviction.Bytes;
                        kept -= eviction.Bytes;
                    }
                }

                sets[inUseSet] = (listed with { Set = (listed.Set with { Bytes = kept }) });
            }

            foreach (var eviction in SelectEvictionsCore(bound: trimTo, entries: sets.Select(selector: static set => set.Set), kept: set => (pinned.Contains(item: set.Path) || IsInUse(entry: set, inUse: inUsePath)), usedSinceUtc: null)) {
                var whole = true;
                var evicted = sets.First(predicate: set => string.Equals(a: set.Set.Path, b: eviction.Path, comparisonType: PuckPaths.Comparison));

                foreach (var entry in (evicted.Entries ?? EntriesOf(held: held, isEntry: isEntry, name: Path.GetFileName(path: eviction.Path), root: root))) {
                    if (TryDelete(directory: held, relativePath: Path.GetRelativePath(path: entry.Path, relativeTo: root))) {
                        removed++;
                        removedBytes += entry.Bytes;
                    } else {
                        whole = false;
                    }
                }
                if (evicted.IsDirectory) {
                    try {
                        // The rest of the set goes through handles too, refusing (and keeping) anything below a link.
                        _ = held.DeleteChild(name: Path.GetFileName(path: eviction.Path));
                    } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                        // A file a reader holds keeps what is left of the set until a later enforcement.
                        whole = false;
                    }
                }
                if (whole) {
                    _ = gone.Add(item: eviction.Path);
                    DeleteLease(set: eviction.Path);
                }
            }
        }

        var total = sets.Sum(selector: static set => set.Count);
        var keptSets = new HashSet<string>(collection: sets.Select(selector: static set => set.Set.Path).Where(predicate: path => !gone.Contains(item: path)), comparer: PuckPaths.Comparer);
        var result = new CacheRetentionResult(
            Kept: (total - removed),
            KeptBytes: (sets.Sum(selector: static set => set.ListedBytes) - removedBytes),
            Removed: removed,
            RemovedBytes: removedBytes
        );

        return new Listing(KeptSets: keptSets, KeptUnits: keptSets.Count, Result: result);
    }
    // One listed set: its total and last use, the entries and bytes the listing found in it, whether it is a directory,
    // and its entries' paths when the listing kept them (the written entry's set, or a file directly in the root).
    private readonly record struct SetListing(CacheEntry Set, List<CacheEntry>? Entries, bool IsDirectory, int Count, long ListedBytes);
    // A set directory's entries, listed again for an eviction: the listing that judged it kept only its total.
    private static List<CacheEntry> EntriesOf(ConfinedDirectory held, string root, string name, Func<string, bool> isEntry) {
        var entries = new List<CacheEntry>();

        try {
            using var child = held.Child(name: name);

            if (child is null) { return entries; }
            foreach (var item in ListFiles(directory: child, recursive: true)) {
                if (isEntry(arg: item.Entry.Name)) {
                    entries.Add(item: new CacheEntry(Bytes: item.Entry.Length, LastUsedUtc: item.Entry.LastWriteTimeUtc, Path: PathOf(relativePath: $"{name}/{item.Path}", root: root)));
                }
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            // A set that can no longer be listed is removed by its directory alone (DeleteChild).
        }
        return entries;
    }
    // The set a written path belongs to: the top-level subdirectory of root holding it, or the file itself directly in root.
    private static string SetOf(string root, string path) {
        var relative = Path.GetRelativePath(path: path, relativeTo: root);
        var separator = relative.IndexOfAny(anyOf: [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

        return ((separator < 0) ? path : Path.Combine(path1: root, path2: relative[..separator]));
    }
    // Holds a cache directory: the caller's root, resolved as the operating system resolves it. Everything below is
    // reached through its handle and never through a link, so no enforcement lists or deletes outside it. A directory
    // that is missing or cannot be opened, or a platform confined access does not support, holds nothing to remove.
    private static ConfinedDirectory? TryOpen(string directory) {
        try {
            return ConfinedDirectory.OpenAnchor(create: false, path: directory);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) {
            return null;
        }
    }
    // The regular files below a held directory, through handles (a link is never listed as a file nor descended), with
    // every temporary left out: one whose writer no longer runs is removed on the way (Abandon), and any other is a
    // write in progress, never an entry.
    private static IEnumerable<ConfinedTreeEntry> ListFiles(ConfinedDirectory directory, bool recursive) {
        var items = (recursive
            ? directory.Walk()
            : directory.Entries().Select(selector: entry => new ConfinedTreeEntry(Directory: directory, Entry: entry, Path: entry.Name)));

        foreach (var item in items) {
            if ((item.Entry.Kind == ConfinedEntryKind.File) && !Abandon(directory: item.Directory, entry: item.Entry)) { yield return item; }
        }
    }
    // Whether a listed file is a temporary, removing it when it has gone unwritten for AbandonedTemporaryAge and its
    // writer is known not to be running (AtomicFile.IsAbandoned): the age only chooses candidates, and the claim decides.
    private static bool Abandon(ConfinedDirectory directory, ConfinedEntry entry) {
        if (!AtomicFile.IsTemporary(path: entry.Name)) { return false; }
        if ((entry.LastWriteTimeUtc < (DateTime.UtcNow - AbandonedTemporaryAge)) && AtomicFile.IsAbandoned(path: entry.Name)) {
            _ = TryDelete(directory: directory, relativePath: entry.Name);
        }
        return true;
    }
    private static string PathOf(string root, string relativePath) => Path.Join(path1: root, path2: relativePath.Replace(newChar: Path.DirectorySeparatorChar, oldChar: '/'));
    // Deletes a file below a held directory through handles, answering whether it is gone: one a reader holds open is in
    // use, and stays, as does one reached through a link.
    private static bool TryDelete(ConfinedDirectory directory, string relativePath) {
        try {
            _ = directory.DeleteFile(relativePath: relativePath);

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
        // What the tally must pass before the next listing where the last one kept more than the bound.
        public long ListAboveBytes;
        public long ListAboveUnits;
        public bool Listed;
        public long ListedAt;
        public HashSet<string>? Sets;
        public long Units;
    }
}
