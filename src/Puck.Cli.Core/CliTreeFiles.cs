namespace Puck.Cli;

/// <summary>File operations over a tree a verb owns that never follow links: the law prover's proof tree and the
/// reference-assembly recovery's build tree. Removing a tree is <c>ConfinedDirectory.RemoveTree</c>.</summary>
public static class CliTreeFiles {
    /// <summary>Generated, vendored and artifact directory names omitted by repository disk walks.</summary>
    public static IReadOnlySet<string> SkippedDirectories { get; } = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase) {
        ".git", ".tmp", "artifacts", "BenchmarkDotNet.Artifacts", "bin", "node_modules", "obj", "publish",
    };

    /// <summary>Finds a linked component from a path up through its ancestors. With a containment root, only the
    /// components strictly below the root are inspected: the root is the caller's own choice (a checkout reached
    /// through a link is still that checkout), so a linked root never refuses the paths under it. An outside path is
    /// refused before inspection. This is input validation, not a race-proof deletion guard.</summary>
    public static string? LinkedAncestor(string path, string? root = null) {
        path = Path.GetFullPath(path: path);
        if ((root is not null) && !Puck.Abstractions.PuckPaths.IsWithin(path: path, root: root)) {
            throw new ArgumentException(message: $"{CliPaths.ToDisplay(fullPath: path)} is outside {CliPaths.ToDisplay(fullPath: root)}.");
        }
        for (var current = path; (current is not null); current = Path.GetDirectoryName(path: current)) {
            if ((root is not null) && CliPaths.Same(left: root, right: current)) { break; }
            if (Path.Exists(path: current) && ((File.GetAttributes(path: current) & FileAttributes.ReparsePoint) != 0)) { return current; }
        }
        return null;
    }
    public static void RequireUnlinkedPath(string path) {
        if (LinkedAncestor(path: path) is { } linked) {
            throw new IOException(message: $"{CliPaths.ToDisplay(fullPath: linked)} is a link in the proven tree or its input path.");
        }
    }
    public static string? LinkedPath(string tree) {
        var pending = new Stack<string>();

        pending.Push(item: tree);
        while (pending.TryPop(result: out var path)) {
            var attributes = File.GetAttributes(path: path);

            if ((attributes & FileAttributes.ReparsePoint) != 0) { return CliPaths.ToDisplay(fullPath: path, relativeTo: tree); }
            if ((attributes & FileAttributes.Directory) != 0) {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path: path)) { pending.Push(item: entry); }
            }
        }
        return null;
    }
    /// <summary>Makes rewritten inputs newer than every file in the tree, including outputs with future timestamps.
    /// Outputs themselves are never retimed. A whole second also separates inputs on coarse timestamp filesystems.</summary>
    public static void Touch(string tree, IEnumerable<string> paths) {
        var latest = DateTime.UtcNow;
        var pending = new Stack<string>();

        pending.Push(item: tree);
        while (pending.TryPop(result: out var directory)) {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path: directory)) {
                if (Path.GetFileName(path: entry) == ".git") { continue; }
                var attributes = File.GetAttributes(path: entry);

                if ((attributes & FileAttributes.ReparsePoint) != 0) { throw new IOException(message: $"{entry} is a link in the proven tree."); }
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(item: entry); } else { latest = new DateTime(ticks: Math.Max(val1: latest.Ticks, val2: File.GetLastWriteTimeUtc(path: entry).Ticks), kind: DateTimeKind.Utc); }
            }
        }
        foreach (var path in paths) {
            var full = Path.Combine(path1: tree, path2: path);

            if (File.Exists(path: full)) { File.SetLastWriteTimeUtc(path: full, lastWriteTimeUtc: latest.AddSeconds(value: 1)); }
        }
    }
}
