using Puck.Abstractions;

namespace Puck.Cli;

/// <summary>Finds the single project owning a directory, with an optional boundary for a disposable proof tree.</summary>
public static class CliProjects {
    public static string? FindOwningProject(string start, string? root = null) {
        for (var directory = new DirectoryInfo(path: start); (directory is not null); directory = directory.Parent) {
            if ((root is not null) && !PuckPaths.IsWithin(root: root, path: directory.FullName)) { break; }
            var projects = directory.GetFiles(searchPattern: "*.csproj");
            // The nearest project directory owns the source; ambiguity must not select a more distant project.
            if (projects.Length != 0) { return ((projects.Length == 1) ? projects[0].FullName : null); }
        }
        return null;
    }
}
