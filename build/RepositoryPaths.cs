namespace Puck;

// Linked into repository tools and verification projects. Runtime files must
// not be located through CallerFilePath: CI rewrites source paths for PDBs.
internal static class RepositoryPaths {
    public static string? FindRoot() =>
        (Ascend(start: AppContext.BaseDirectory) ?? Ascend(start: Environment.CurrentDirectory));
    public static string Resolve(string relativePath) =>
        Path.Combine(
            path1: (FindRoot() ?? throw new DirectoryNotFoundException(message: $"No Puck.slnx above {AppContext.BaseDirectory} or {Environment.CurrentDirectory}.")),
            path2: relativePath
        );

    private static string? Ascend(string start) {
        for (var directory = new DirectoryInfo(path: start); (directory is not null); directory = directory.Parent) {
            if (File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
                return directory.FullName;
            }
        }

        return null;
    }
}
