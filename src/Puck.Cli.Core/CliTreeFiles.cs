namespace Puck.Cli;

/// <summary>File operations over a tree a verb owns that never follow links, including when removing a damaged cache:
/// the law prover's proof tree and the reference-assembly recovery's build tree.</summary>
public static class CliTreeFiles {
    public static void RequireUnlinkedPath(string path) {
        for (var current = Path.GetFullPath(path: path); (current is not null); current = Path.GetDirectoryName(path: current)) {
            if (Path.Exists(path: current) && ((File.GetAttributes(path: current) & FileAttributes.ReparsePoint) != 0)) {
                throw new IOException(message: $"{CliPaths.ToDisplay(fullPath: current)} is a link in the proven tree or its input path.");
            }
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
    public static void DeleteTree(string path) {
        if (!Path.Exists(path: path)) { return; }
        var attributes = File.GetAttributes(path: path);

        if ((attributes & FileAttributes.Directory) != 0) {
            if ((attributes & FileAttributes.ReparsePoint) == 0) {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path: path)) { DeleteTree(path: entry); }
            }
            Directory.Delete(path: path);
        } else {
            if ((attributes & FileAttributes.ReparsePoint) == 0) { File.SetAttributes(fileAttributes: attributes & ~FileAttributes.ReadOnly, path: path); }
            File.Delete(path: path);
        }
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
