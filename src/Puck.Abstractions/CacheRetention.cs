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
/// (<see cref="EnforceOnWrite"/>, or <see cref="SelectEvictions"/> where it removes entries its own way): entries are
/// ordered most recently used first, equal stamps falling to the ordinally lesser path, the entry in use is kept
/// whatever its stamp and counts against the bound first, and the rest are kept while both the count and the bytes stay
/// within the bound. From the first entry that does not fit, every older one goes. An entry that cannot be removed
/// because a reader holds it open is in use, and stays.
/// </para>
/// <para>
/// A stamp is file-system metadata and a cache's contents are content-addressed, so retention never decides what a
/// cache answers: an evicted entry costs its next reader a derivation, never a different result.
/// </para>
/// </summary>
public static class CacheRetention {
    /// <summary>How long after one enforcement of a directory a process skips the next: a writer that publishes many
    /// entries in a row lists the directory once, not once per entry.</summary>
    public static readonly TimeSpan EnforceInterval = TimeSpan.FromMinutes(minutes: 1);

    private static readonly ConcurrentDictionary<string, long> Enforced = new(comparer: StringComparer.Ordinal);

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

        var removed = 0;
        var removedBytes = 0L;

        foreach (var eviction in SelectEvictions(bound: bound, entries: entries, inUse: ((inUse is null) ? null : Path.GetFullPath(path: inUse)), usedSinceUtc: usedSinceUtc)) {
            try {
                File.Delete(path: eviction.Path);
                removed++;
                removedBytes += eviction.Bytes;
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                // Held open by a reader, so in use: it stays.
            }
        }

        return new CacheRetentionResult(
            Kept: (entries.Count - removed),
            KeptBytes: (entries.Sum(selector: static entry => entry.Bytes) - removedBytes),
            Removed: removed,
            RemovedBytes: removedBytes
        );
    }
    /// <summary>Enforces <paramref name="bound"/> on a directory (<see cref="Enforce"/>) unless this process already did
    /// within <see cref="EnforceInterval"/>: what an owner calls after each write.</summary>
    /// <param name="directory">The cache directory.</param>
    /// <param name="bound">The bound.</param>
    /// <param name="isEntry">Which files are entries.</param>
    /// <param name="search">Whether entries lie in subdirectories too.</param>
    /// <param name="inUse">The entry the owner just wrote or is using, or <see langword="null"/>.</param>
    /// <returns>Whether the enforcement ran.</returns>
    public static bool EnforceOnWrite(string directory, CacheBound bound, Func<FileInfo, bool> isEntry, SearchOption search = SearchOption.TopDirectoryOnly, string? inUse = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);

        var key = Path.GetFullPath(path: directory);
        var now = Environment.TickCount64;
        var interval = ((long)EnforceInterval.TotalMilliseconds);

        while (true) {
            if (Enforced.TryGetValue(key: key, value: out var last)) {
                if ((now - last) < interval) {
                    return false;
                }

                if (!Enforced.TryUpdate(comparisonValue: last, key: key, newValue: now)) {
                    continue;
                }
            } else if (!Enforced.TryAdd(key: key, value: now)) {
                continue;
            }

            break;
        }

        _ = Enforce(bound: bound, directory: key, inUse: inUse, isEntry: isEntry, search: search);

        return true;
    }

    private static bool IsInUse(CacheEntry entry, string? inUse) =>
        ((inUse is not null) && string.Equals(a: entry.Path, b: inUse, comparisonType: PuckPaths.Comparison));
}
