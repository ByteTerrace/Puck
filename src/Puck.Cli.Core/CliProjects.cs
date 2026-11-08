using Puck.Abstractions;

namespace Puck.Cli;

/// <summary>Finds the single project owning a directory, with an optional boundary for a disposable proof tree.</summary>
public static class CliProjects {
    /// <summary>The one project file in the nearest directory (from <paramref name="start"/> up) that holds any, or
    /// <see langword="null"/> when that directory holds several: ambiguity never selects a more distant project.</summary>
    public static string? FindOwningProject(string start, string? root = null) =>
        (Nearest(start: start, root: root) is { Length: 1 } projects) ? projects[0].FullName : null;
    /// <summary>The nearest directory (from <paramref name="start"/> up) that holds any project file, however many it
    /// holds: the directory a source's build is rooted at.</summary>
    public static string? FindOwningProjectDirectory(string start, string? root = null) =>
        Nearest(start: start, root: root)?[0].DirectoryName;

    private static FileInfo[]? Nearest(string start, string? root) {
        for (var directory = new DirectoryInfo(path: start); (directory is not null); directory = directory.Parent) {
            if ((root is not null) && !PuckPaths.IsWithin(root: root, path: directory.FullName)) { break; }
            var projects = directory.GetFiles(searchPattern: "*.csproj");

            if (projects.Length != 0) { return projects; }
        }
        return null;
    }
}
