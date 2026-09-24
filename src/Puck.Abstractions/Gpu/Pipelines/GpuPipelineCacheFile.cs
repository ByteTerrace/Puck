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
/// Each backend keeps at most <see cref="RetainedFiles"/> files, least recently used first out. Opening a file, and every
/// creation it answers, moves its last-write time to now. Opening a file then keeps the backend's most recently written
/// files across all its device directories, the opened file always among them, deletes the rest, counting each as
/// <see cref="GpuWork.PipelineCachePruned"/>, and removes every device directory left empty; equal times fall to the
/// ordinally lesser path. Worktrees and sessions of different commits sharing one state root therefore keep each
/// other's files while a directory named for an old driver or key format ages out. A temporary is never deleted and
/// keeps its directory, another backend's files are never touched, and a file another process holds open is reported
/// on standard error and left in place.
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

    private const string Extension = ".bin";

    private readonly GpuPipelineCacheWork m_work;

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
            Console.Error.WriteLine(value: $"[pipeline-cache] not pruned {Slashed(path: backend.FullName)}: {exception.Message}");

            return;
        }

        // The entries each device directory still holds; one that could not be listed is marked -1 and never removed.
        var remaining = new int[devices.Length];
        var candidates = new List<(FileInfo File, string Name, int Device)>();

        for (var device = 0; (device < devices.Length); device++) {
            FileSystemInfo[] entries;

            try {
                entries = devices[device].GetFileSystemInfos();
            } catch (Exception exception) when ((exception is DirectoryNotFoundException)) {
                remaining[device] = -1;

                continue;
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                Console.Error.WriteLine(value: $"[pipeline-cache] not pruned {Slashed(path: devices[device].FullName)}: {exception.Message}");
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
                    candidates.Add(item: (file, Slashed(path: file.FullName), device));
                }
            }
        }

        candidates.Sort(comparison: static (left, right) => {
            var order = right.File.LastWriteTimeUtc.CompareTo(value: left.File.LastWriteTimeUtc);

            return ((order != 0) ? order : string.CompareOrdinal(
                strA: left.Name,
                strB: right.Name
            ));
        });

        // The opened file holds one of the places: it is the one in use, and a miss writes it.
        for (var index = (RetainedFiles - 1); (index < candidates.Count); index++) {
            var (file, name, device) = candidates[index];

            try {
                file.Delete();
                remaining[device]--;
                work.CountPruned();
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                Console.Error.WriteLine(value: $"[pipeline-cache] not pruned {name}: {exception.Message}");
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
                Console.Error.WriteLine(value: $"[pipeline-cache] not removed {Slashed(path: devices[device].FullName)}: {exception.Message}");
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
    private static string Slashed(string path) =>
        path.Replace(
            newChar: '/',
            oldChar: '\\'
        );
    private bool Save(string path, ReadOnlySpan<byte> data) {
        var temporary = $"{path}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";

        try {
            Directory.CreateDirectory(path: System.IO.Path.GetDirectoryName(path: path)!);

            using (var stream = new FileStream(
                access: FileAccess.Write,
                mode: FileMode.CreateNew,
                path: temporary,
                share: FileShare.None
            )) {
                stream.Write(buffer: data);
                stream.Flush(flushToDisk: true);
            }

            File.Move(
                destFileName: path,
                overwrite: true,
                sourceFileName: temporary
            );

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            try {
                File.Delete(path: temporary);
            } catch (Exception cleanup) when ((cleanup is IOException or UnauthorizedAccessException)) {
                // The temporary name is unique to this write; a leftover is never read.
            }

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
    /// <summary>Counts one pipeline created through the cache. A hit refreshes the file's last-write time; a miss leaves
    /// a write owed to the next <see cref="Persist{TState}"/>.</summary>
    /// <param name="cacheHit">Whether the driver answered the creation from its cache.</param>
    public void Count(bool cacheHit) {
        m_work.Count(cacheHit: cacheHit);

        if (!cacheHit) {
            _ = Interlocked.Increment(location: ref m_unsaved);
        } else if (Path is not null) {
            Refresh(path: Path);
        }
    }
    /// <summary>Writes the cache to the file when a creation missed it since the last write. The caller serializes
    /// calls; a failed serialization is reported by the backend and drops the owed write, and a failed file write is
    /// reported and leaves it owed, so the next call retries.</summary>
    /// <typeparam name="TState">What <paramref name="serialize"/> reads.</typeparam>
    /// <param name="state">The backend's cache.</param>
    /// <param name="serialize">Serializes the backend's cache; empty when there is nothing to write or it failed
    /// (reported). Called only when a write is owed and the file is on disk.</param>
    public void Persist<TState>(TState state, Func<TState, ReadOnlyMemory<byte>> serialize) {
        ArgumentNullException.ThrowIfNull(argument: serialize);

        var unsaved = Interlocked.Exchange(
            location1: ref m_unsaved,
            value: 0L
        );

        if ((Path is null) || (unsaved == 0L)) {
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
