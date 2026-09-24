namespace Puck;

// Linked into repository tools and verification projects. Runtime files must
// not be located through CallerFilePath: CI rewrites source paths for PDBs.
// The repository is the one containing the working directory; the running
// assembly's own checkout is only the fallback, so a tool built in one checkout
// and run from another acts on the one it was run from.
internal static class RepositoryPaths {
    // The marker walk every lookup shares: try `probe` at `start`, then each parent in turn, stopping at the first
    // non-null result or at the file system root.
    public static string? Ascend(string start, Func<DirectoryInfo, string?> probe) {
        for (var directory = new DirectoryInfo(path: start); (directory is not null); directory = directory.Parent) {
            if (probe(arg: directory) is { } match) {
                return match;
            }
        }

        return null;
    }
    public static string? FindRoot() =>
        (FindRootAbove(start: Environment.CurrentDirectory) ?? FindRootAbove(start: AppContext.BaseDirectory));
    // The root, or the one refusal every caller that cannot proceed without a checkout throws.
    public static string RequireRoot() =>
        (FindRoot() ?? throw new DirectoryNotFoundException(message: $"No Puck.slnx above {Environment.CurrentDirectory} or {AppContext.BaseDirectory}; run within a Puck checkout."));
    public static string Resolve(string relativePath) =>
        Path.Combine(
            path1: RequireRoot(),
            path2: relativePath
        );

    private static string? FindRootAbove(string start) =>
        Ascend(
            probe: static directory => (File.Exists(path: Path.Combine(
                path1: directory.FullName,
                path2: "Puck.slnx"
            ))
                ? directory.FullName
                : null),
            start: start
        );
}
