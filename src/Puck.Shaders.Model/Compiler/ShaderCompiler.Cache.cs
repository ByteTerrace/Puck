using Puck.Abstractions;

namespace Puck.Shaders;

public sealed partial class ShaderCompiler {
    /// <summary>The bound every cache directory a compiler publishes into is held to: the most recently used outputs
    /// within it stay (<see cref="CacheRetention"/>).</summary>
    public static readonly CacheBound Retention = new(MaxBytes: (1L << 30), MaxEntries: 4096);

    /// <summary>Removes every file of a cache directory that no compile has used since <paramref name="cutoffUtc"/>:
    /// each entry, duration record and abandoned staged publication whose last write time is earlier. A compiler stamps
    /// each entry and duration record it reads, and writes each one it publishes, so a file's last write time is when a
    /// compile last used it.</summary>
    /// <remarks>Only files the compiler writes are considered: <c>*.spv</c>, <c>*.dxil</c> and <c>*.tmp</c> at the
    /// cache's top level and every file in its <c>durations</c> directory. Anything else stays. A file a reader still
    /// holds, which the operating system will not delete, is kept and counted as kept. A build sharing the directory while
    /// it is pruned loses nothing it needs: an entry removed between its plan and its read compiles again. The age bound
    /// is <see cref="CacheRetention"/>'s, so this and the <see cref="Retention"/> a publication enforces are one policy.</remarks>
    /// <param name="cacheDirectory">The cache directory. One that does not exist holds nothing to remove.</param>
    /// <param name="cutoffUtc">The earliest last write time a file keeps, in UTC.</param>
    /// <returns>The files removed and kept.</returns>
    public static CacheRetentionResult Prune(string cacheDirectory, DateTime cutoffUtc) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: cacheDirectory);

        var unbounded = new CacheBound(MaxBytes: long.MaxValue, MaxEntries: int.MaxValue);
        var outputs = CacheRetention.Enforce(
            bound: unbounded,
            directory: cacheDirectory,
            isEntry: static file => (file.Extension is ".spv" or ".dxil" or ".tmp"),
            usedSinceUtc: cutoffUtc
        );
        var durations = CacheRetention.Enforce(
            bound: unbounded,
            directory: Path.Combine(path1: cacheDirectory, path2: DurationsDirectory),
            isEntry: static _ => true,
            usedSinceUtc: cutoffUtc
        );

        return new CacheRetentionResult(
            Kept: (outputs.Kept + durations.Kept),
            KeptBytes: (outputs.KeptBytes + durations.KeptBytes),
            Removed: (outputs.Removed + durations.Removed),
            RemovedBytes: (outputs.RemovedBytes + durations.RemovedBytes)
        );
    }

    // Holds the cache within Retention after a publication, at most once a minute per directory in a process, keeping the
    // entry just published. Only published outputs count; a staged publication of another compile is never touched.
    private void Retain(string published) => _ = CacheRetention.EnforceOnWrite(
        bound: Retention,
        directory: m_cacheDirectory,
        inUse: published,
        isEntry: static file => (file.Extension is ".spv" or ".dxil")
    );
    // Records that a compile used a cache file now, so a prune keeps it. Losing a stamp costs at most a later compile
    // of that output, never a wrong result, so a file that cannot be stamped is left as it is.
    private static void Stamp(string path) => CacheRetention.Stamp(path: path);

}
