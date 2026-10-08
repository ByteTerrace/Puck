namespace Puck.Shaders;

/// <summary>What <see cref="ShaderCompiler.Prune"/> removed from a cache directory and what it kept.</summary>
/// <param name="Removed">The files removed: entries, duration records and abandoned staged publications.</param>
/// <param name="RemovedBytes">Their total length.</param>
/// <param name="Kept">The files kept, because a compile used them since the cutoff or a reader still held them.</param>
/// <param name="KeptBytes">Their total length.</param>
public readonly record struct ShaderCachePruneResult(int Removed, long RemovedBytes, int Kept, long KeptBytes);
public sealed partial class ShaderCompiler {
    /// <summary>Removes every file of a cache directory that no compile has used since <paramref name="cutoffUtc"/>:
    /// each entry, duration record and abandoned staged publication whose last write time is earlier. A compiler stamps
    /// each entry and duration record it reads, and writes each one it publishes, so a file's last write time is when a
    /// compile last used it.</summary>
    /// <remarks>Only files the compiler writes are considered: <c>*.spv</c>, <c>*.dxil</c> and <c>*.tmp</c> at the
    /// cache's top level and every file in its <c>durations</c> directory. Anything else stays. A file a reader still
    /// holds, which the operating system will not delete, is kept and counted as kept. A build sharing the directory while
    /// it is pruned loses nothing it needs: an entry removed between its plan and its read compiles again.</remarks>
    /// <param name="cacheDirectory">The cache directory. One that does not exist holds nothing to remove.</param>
    /// <param name="cutoffUtc">The earliest last write time a file keeps, in UTC.</param>
    /// <returns>The files removed and kept.</returns>
    public static ShaderCachePruneResult Prune(string cacheDirectory, DateTime cutoffUtc) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: cacheDirectory);

        var result = new ShaderCachePruneResult();
        var root = new DirectoryInfo(path: cacheDirectory);

        if (!root.Exists) {
            return result;
        }

        var durations = new DirectoryInfo(path: Path.Combine(path1: root.FullName, path2: DurationsDirectory));
        var files = root.EnumerateFiles().Where(predicate: static file => (file.Extension is ".spv" or ".dxil" or ".tmp"))
            .Concat(second: (durations.Exists ? durations.EnumerateFiles() : []));

        foreach (var file in files) {
            var length = file.Length;

            if (file.LastWriteTimeUtc < cutoffUtc) {
                try {
                    file.Delete();
                    result = result with { Removed = (result.Removed + 1), RemovedBytes = (result.RemovedBytes + length) };

                    continue;
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                    // Held open by a reader, so in use: it stays.
                }
            }

            result = result with { Kept = (result.Kept + 1), KeptBytes = (result.KeptBytes + length) };
        }

        return result;
    }

    // Records that a compile used a cache file now, so a prune keeps it. Losing a stamp costs at most a later compile
    // of that output, never a wrong result, so a file that cannot be stamped is left as it is.
    private static void Stamp(string path) {
        try {
            File.SetLastWriteTimeUtc(
                lastWriteTimeUtc: DateTime.UtcNow,
                path: path
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) { }
    }
}
