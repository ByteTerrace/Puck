using Puck.Abstractions;

namespace Puck.Cli.Counters;

/// <summary>The driver pipeline cache every counters leg starts from and writes back, one directory per backend under
/// the per-user Puck directory. A leg boots its World in a fresh state directory, whose empty <c>pipeline-cache</c>
/// would otherwise make the driver build every pipeline again; the cache's hits and misses are pacing counts, so a
/// warm cache changes no count a report compares or a ceiling judges.</summary>
public static class CountersPipelineCache {
    /// <summary>The per-user directory's name under <see cref="PuckUserDirectory.Root"/>.</summary>
    public const string DirectoryName = "counters-pipeline-cache";

    /// <summary>Gets the per-user cache root the counters verb uses.</summary>
    public static string DefaultRoot => PuckUserDirectory.Resolve(name: DirectoryName);

    /// <summary>Copies the persisted cache of <paramref name="backend"/> into a leg's state directory before its World
    /// boots. Nothing is copied when no cache has been persisted.</summary>
    /// <param name="root">The cache root, <see cref="DefaultRoot"/> outside laws.</param>
    /// <param name="stateDirectory">The leg's World state directory.</param>
    /// <param name="backend">The leg's backend name.</param>
    /// <returns>The number of files copied.</returns>
    public static int Seed(string root, string stateDirectory, string backend) => Copy(
        from: Path.Combine(path1: root, path2: backend),
        prune: false,
        to: Path.Combine(path1: stateDirectory, path2: "pipeline-cache")
    );
    /// <summary>Replaces the persisted cache of <paramref name="backend"/> with a completed leg's, file by file, and
    /// removes the persisted files the leg's World pruned.</summary>
    /// <param name="root">The cache root, <see cref="DefaultRoot"/> outside laws.</param>
    /// <param name="stateDirectory">The leg's World state directory, after its World exited.</param>
    /// <param name="backend">The leg's backend name.</param>
    /// <returns>The number of files written.</returns>
    public static int Persist(string root, string stateDirectory, string backend) => Copy(
        from: Path.Combine(path1: stateDirectory, path2: "pipeline-cache"),
        prune: true,
        to: Path.Combine(path1: root, path2: backend)
    );

    // Each file is written beside its destination and moved over it, so a reader never sees a partial cache file. The
    // World writes its cache files the same way and leaves its own temporaries, which are never carried.
    private static int Copy(string from, string to, bool prune) {
        if (!Directory.Exists(path: from)) { return 0; }
        var copied = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var source in Directory.EnumerateFiles(path: from, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            if (source.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".tmp")) { continue; }
            var relative = Path.GetRelativePath(path: source, relativeTo: from);
            var destination = Path.Combine(path1: to, path2: relative);
            var staging = $"{destination}.{Environment.ProcessId}.tmp";

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
            File.Copy(destFileName: staging, overwrite: true, sourceFileName: source);
            File.Move(destFileName: destination, overwrite: true, sourceFileName: staging);
            copied.Add(item: relative);
        }
        if (prune && Directory.Exists(path: to)) {
            foreach (var stale in Directory.EnumerateFiles(path: to, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
                if (!copied.Contains(item: Path.GetRelativePath(path: stale, relativeTo: to))) { File.Delete(path: stale); }
            }
        }
        return copied.Count;
    }
}
