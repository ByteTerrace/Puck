using Puck.Confinement;
namespace Puck.Abstractions.Gpu;

/// <summary>
/// One device's persistent pipeline-cache file, and the policy every backend shares for it: where it lives, when it is
/// written, and how it is read and replaced. The backend keeps only what is native to it: serializing its cache,
/// creating one from the file's bytes, and refusing bytes its driver would not accept.
/// <para>
/// The file is <c>&lt;directory&gt;/&lt;backend&gt;/&lt;device&gt;/&lt;contentKey&gt;.bin</c>: the backend and
/// <see cref="GpuDeviceIdentity.CacheKey"/> from the device's identity, the directory and content key from the
/// <see cref="GpuPipelineCacheStore"/>. A driver update or a kernel change therefore starts a new file instead of loading
/// one that could never hit.
/// </para>
/// <para>
/// Each backend keeps at most <see cref="RetainedFiles"/> files, least recently used first out (<see cref="CacheRetention"/>). Opening a file moves
/// its last-write time to now, and so does the next write once a creation it answers. Opening a file then keeps the backend's most recently written
/// files across all its device directories, the opened file always among them, deletes the rest, counting each as
/// <see cref="GpuWork.PipelineCachePruned"/>, and removes every device directory left empty; equal times fall to the
/// ordinally lesser path. Worktrees and sessions of different commits sharing one state root therefore keep each
/// other's files while a directory named for an old driver or key format ages out. A temporary is never deleted and
/// keeps its directory, another backend's files are never touched, and a file another process holds open is reported
/// on standard error and left in place.
/// </para>
/// <para>
/// The file is written in the background, never by the thread that created a pipeline: once a backend
/// <see cref="Attach"/>es its serializer, a miss owes a write (and a hit a refresh of the last-write time), which runs on
/// the thread pool once creations have been quiet for <see cref="WriteQuiet"/>, or once the oldest owed one has waited
/// <see cref="WriteLatest"/>, whichever comes first (<see cref="QuietTrigger"/>). A cold start creating pipelines for a
/// minute therefore writes the file a handful of times rather than once a pipeline, and its bytes written grow with the
/// cache, not with its square. <see cref="Close"/> writes what is still owed when the backend's cache goes away.
/// </para>
/// <para>
/// Several processes may share the directory. A write goes to a temporary file beside the target and then replaces the
/// target in one rename, so a reader sees a whole old file or a whole new one, never a partial one; concurrent writers
/// each replace the file whole, and the last one wins. A rename another process blocks (on Windows, by holding the file
/// open) fails the write, leaves the old file whole, removes the temporary, and leaves the write owed to the next
/// <see cref="Persist{TState}"/>. A file that is missing, unreadable or refused by the backend is reported by name on
/// standard error and treated as empty: a cache never stops a device.
/// </para>
/// </summary>
public sealed class GpuPipelineCacheFile {
    /// <summary>The most kernel-set files one backend keeps on disk, counted across every device directory under it.
    /// Opening a file deletes the least recently written files beyond this many.</summary>
    public const int RetainedFiles = 8;

    /// <summary>How long creations must stop before an owed write runs.</summary>
    public static readonly TimeSpan WriteQuiet = TimeSpan.FromSeconds(seconds: 2L);
    /// <summary>The longest an owed write waits while creations keep coming.</summary>
    public static readonly TimeSpan WriteLatest = TimeSpan.FromSeconds(seconds: 10L);

    private const string Extension = ".bin";

    private readonly GpuPipelineCacheWork m_work;
    // Serializes writes, so a background write and the closing one never interleave and an older serialization never
    // replaces a newer one.
    private readonly Lock m_writeGate = new();

    private Func<ReadOnlyMemory<byte>>? m_serialize;
    private QuietTrigger? m_trigger;
    private int m_touched;
    private long m_unsaved;

    private GpuPipelineCacheFile(string? path, GpuPipelineCacheWork work) {
        m_work = work;
        Path = path;
    }

    /// <summary>Gets the absolute file, with forward slashes, or <see langword="null"/> when the cache is kept in memory
    /// only.</summary>
    public string? Path { get; }

    // Keeps the backend's RetainedFiles most recently written kernel-set files across every device directory under
    // it, the opened file always among them whether or not it exists yet, deletes the rest, and removes each device
    // directory left empty. Equal times fall to the ordinally lesser path, so the choice never depends on listing
    // order. Only .bin files are candidates: a temporary another writer is still renaming ends in .tmp, so it is never
    // deleted and keeps its directory alive. A missing backend directory holds nothing to prune.
    private static void Prune(string path, GpuPipelineCacheWork work) {
        var opened = System.IO.Path.GetFullPath(path: path);
        var backend = new DirectoryInfo(path: System.IO.Path.GetDirectoryName(path: System.IO.Path.GetDirectoryName(path: opened))!);
        DirectoryInfo[] devices;

        try {
            devices = backend.GetDirectories();
        } catch (Exception exception) when ((exception is DirectoryNotFoundException)) {
            return;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[pipeline-cache] not pruned {PuckPaths.Slashed(path: backend.FullName)}: {exception.Message}");

            return;
        }

        // The entries each device directory still holds; one that could not be listed is marked -1 and never removed.
        var remaining = new int[devices.Length];
        var candidates = new List<CacheEntry>();
        var deviceOf = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        for (var device = 0; (device < devices.Length); device++) {
            FileSystemInfo[] entries;

            try {
                entries = devices[device].GetFileSystemInfos();
            } catch (Exception exception) when ((exception is DirectoryNotFoundException)) {
                remaining[device] = -1;

                continue;
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                Console.Error.WriteLine(value: $"[pipeline-cache] not pruned {PuckPaths.Slashed(path: devices[device].FullName)}: {exception.Message}");
                remaining[device] = -1;

                continue;
            }

            remaining[device] = entries.Length;

            foreach (var entry in entries) {
                if ((entry is FileInfo file) && file.Name.EndsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: Extension
                ) && !string.Equals(
                    a: file.FullName,
                    b: opened,
                    comparisonType: StringComparison.Ordinal
                )) {
                    var name = PuckPaths.Slashed(path: file.FullName);

                    candidates.Add(item: new CacheEntry(Bytes: 0, LastUsedUtc: file.LastWriteTimeUtc, Path: name));
                    deviceOf[name] = device;
                }
            }
        }

        // The opened file holds one of the places whether or not it exists yet: it is the one in use, and a miss writes it.
        var inUse = PuckPaths.Slashed(path: opened);

        candidates.Add(item: new CacheEntry(Bytes: 0, LastUsedUtc: DateTime.MaxValue, Path: inUse));
        foreach (var eviction in CacheRetention.SelectEvictions(bound: CacheBound.Entries(maxEntries: RetainedFiles), entries: candidates, inUse: inUse)) {
            var device = deviceOf[eviction.Path];

            try {
                File.Delete(path: eviction.Path);
                remaining[device]--;
                work.CountPruned();
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                Console.Error.WriteLine(value: $"[pipeline-cache] not pruned {eviction.Path}: {exception.Message}");
            }
        }

        for (var device = 0; (device < devices.Length); device++) {
            if (remaining[device] != 0) {
                continue;
            }

            try {
                devices[device].Delete();
            } catch (Exception exception) when ((exception is DirectoryNotFoundException)) {
                // Another process removed it first.
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                Console.Error.WriteLine(value: $"[pipeline-cache] not removed {PuckPaths.Slashed(path: devices[device].FullName)}: {exception.Message}");
            }
        }
    }
    // Moves the file's last-write time to now, so the backend's pruning takes it for recently used. A file not yet
    // written has nothing to refresh; the time is file-system metadata, never simulation state.
    private static void Refresh(string path) {
        try {
            File.SetLastWriteTimeUtc(
                lastWriteTimeUtc: DateTime.UtcNow,
                path: path
            );
        } catch (Exception exception) when ((exception is FileNotFoundException or DirectoryNotFoundException)) {
            // Nothing is on disk yet; the first write stamps the file.
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[pipeline-cache] not refreshed {path}: {exception.Message}");
        }
    }

    private bool Save(string path, ReadOnlySpan<byte> data) {
        try {
            AtomicFile.WriteAllBytes(
                bytes: data,
                path: path
            );

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[pipeline-cache] not written {path}: {exception.Message}");

            return false;
        }
    }

    /// <summary>Opens one device's cache file, refreshing its last-write time and deleting the backend's least recently
    /// written files beyond <see cref="RetainedFiles"/>.</summary>
    /// <param name="store">Where caches live on disk, or <see langword="null"/> to keep this one in memory only.</param>
    /// <param name="identity">The device's identity, as its backend reported it when the device was created.</param>
    /// <param name="work">The backend's pipeline counts, which every creation through this file and every pruned file
    /// adds to.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> or <paramref name="work"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The identity's backend is not one path segment of lowercase letters, digits
    /// and hyphens.</exception>
    public static GpuPipelineCacheFile Open(GpuPipelineCacheStore? store, GpuDeviceIdentity identity, GpuPipelineCacheWork work) {
        ArgumentNullException.ThrowIfNull(argument: identity);
        ArgumentNullException.ThrowIfNull(argument: work);
        GpuPipelineCacheStore.RequireSegment(
            paramName: nameof(identity),
            segment: identity.Backend
        );

        if (store is null) {
            return new GpuPipelineCacheFile(
                path: null,
                work: work
            );
        }

        var path = $"{store.Directory}/{identity.Backend}/{identity.CacheKey}/{store.ContentKey}{Extension}";

        Refresh(path: path);
        Prune(
            path: path,
            work: work
        );

        return new GpuPipelineCacheFile(
            path: path,
            work: work
        );
    }
    /// <summary>Binds the backend's serializer, so a write a creation owes runs on the thread pool once creations go
    /// quiet, and <see cref="Close"/> writes what is still owed. A file kept in memory only binds nothing.</summary>
    /// <param name="serialize">Serializes the backend's cache on whatever thread writes; empty when the cache is gone or
    /// its serialization failed (reported by the backend). It synchronizes with the backend's own creations and
    /// release.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serialize"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A serializer is already bound.</exception>
    public void Attach(Func<ReadOnlyMemory<byte>> serialize) {
        ArgumentNullException.ThrowIfNull(argument: serialize);

        if (Path is null) {
            return;
        }

        if (Interlocked.CompareExchange(comparand: null, location1: ref m_serialize, value: serialize) is not null) {
            throw new InvalidOperationException(message: "The pipeline-cache file already has a serializer.");
        }

        _ = Interlocked.Exchange(
            location1: ref m_trigger,
            value: new QuietTrigger(
                action: Flush,
                latest: WriteLatest,
                quiet: WriteQuiet
            )
        );
    }
    /// <summary>Stops the background writes and writes what is still owed with the bound serializer, on the calling
    /// thread. The backend calls it before it releases its cache; a later creation's count owes a write no one makes.
    /// Closing twice writes nothing more.</summary>
    public void Close() {
        if (Interlocked.Exchange(location1: ref m_trigger, value: null) is { } trigger) {
            trigger.Dispose();
        }

        Flush();
    }
    /// <summary>Counts one pipeline created through the cache. A miss owes a write and a hit a refresh of the file's
    /// last-write time; both run in the background once a serializer is bound (<see cref="Attach"/>), or at the next
    /// <see cref="Persist{TState}"/>.</summary>
    /// <param name="cacheHit">Whether the driver answered the creation from its cache.</param>
    public void Count(bool cacheHit) {
        m_work.Count(cacheHit: cacheHit);

        if (!cacheHit) {
            _ = Interlocked.Increment(location: ref m_unsaved);
        } else {
            Volatile.Write(location: ref m_touched, value: 1);
        }

        Volatile.Read(location: ref m_trigger)?.Request();
    }
    /// <summary>Writes the cache to the file now when a creation missed it since the last write, or refreshes the
    /// file's last-write time when one only hit it. Writes are serialized; a failed serialization is reported by the
    /// backend and drops the owed write, and a failed file write is reported and leaves it owed, so the next write
    /// retries.</summary>
    /// <typeparam name="TState">What <paramref name="serialize"/> reads.</typeparam>
    /// <param name="state">The backend's cache.</param>
    /// <param name="serialize">Serializes the backend's cache; empty when there is nothing to write or it failed
    /// (reported). Called only when a write is owed and the file is on disk.</param>
    public void Persist<TState>(TState state, Func<TState, ReadOnlyMemory<byte>> serialize) {
        ArgumentNullException.ThrowIfNull(argument: serialize);

        lock (m_writeGate) {
            var unsaved = Interlocked.Exchange(
                location1: ref m_unsaved,
                value: 0L
            );
            var touched = (Interlocked.Exchange(location1: ref m_touched, value: 0) != 0);

            if (Path is null) {
                return;
            }

            if (unsaved == 0L) {
                if (touched) {
                    Refresh(path: Path);
                }

                return;
            }

            var data = serialize(arg: state);

            if (data.IsEmpty) {
                return;
            }

            if (!Save(
                data: data.Span,
                path: Path
            )) {
                _ = Interlocked.Add(
                    location1: ref m_unsaved,
                    value: unsaved
                );
            }
        }
    }
    // The background write and the closing one: what is owed, through the bound serializer.
    private void Flush() {
        if (Volatile.Read(location: ref m_serialize) is { } serialize) {
            Persist(serialize: static serialize => serialize(), state: serialize);
        }
    }
    /// <summary>Reads the file.</summary>
    /// <returns>The file's bytes, or <see langword="null"/> when the cache is kept in memory only, the file does not
    /// exist, or it could not be read (reported).</returns>
    public byte[]? Read() {
        if (Path is null) {
            return null;
        }

        try {
            using var stream = new FileStream(
                access: FileAccess.Read,
                mode: FileMode.Open,
                path: Path,
                share: FileShare.ReadWrite | FileShare.Delete
            );
            var bytes = new byte[stream.Length];

            stream.ReadExactly(buffer: bytes);

            return bytes;
        } catch (Exception exception) when ((exception is FileNotFoundException or DirectoryNotFoundException)) {
            return null;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Refuse(reason: exception.Message);

            return null;
        }
    }
    /// <summary>Reports that the file's bytes were not used, and why. The file stays where it is; the next write
    /// replaces it.</summary>
    /// <param name="reason">Why it was discarded: a foreign device, a failed validation, a read error.</param>
    public void Refuse(string reason) =>
        Console.Error.WriteLine(value: $"[pipeline-cache] discarded {Path}: {reason}");
}
