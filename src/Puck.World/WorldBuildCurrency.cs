using System.IO.Enumeration;
using Puck.Abstractions;

namespace Puck.World;

/// <summary>
/// Answers whether the running World was built before the engine sources in its checkout last changed: for each engine
/// assembly beside the running one (<c>Puck*.dll</c>) whose project lives in the checkout (<c>src/&lt;assembly&gt;/</c>), any
/// C# source or project file written after that assembly was. A document that uses what such a source added (a rule
/// function, a vocabulary member, a validation rule) is refused by the running build until the World restarts on a new
/// one, and the refusal reads as a document error; this names the restart owed instead.
/// </summary>
/// <remarks>It reads directory listings only (each entry's last write comes with the listing), on whatever thread asks:
/// a reload asks off the pump, after a refusal. A checkout the World was not built from (another worktree, a player
/// install with no sources) answers nothing.</remarks>
internal static class WorldBuildCurrency {
    /// <summary>One engine source written after the assembly it compiles into.</summary>
    /// <param name="Path">The source's path, relative to the checkout, with forward slashes.</param>
    /// <param name="Written">When the source was last written, in UTC.</param>
    /// <param name="Built">When the running build wrote the assembly the source belongs to, in UTC.</param>
    public readonly record struct StaleSource(string Path, DateTime Written, DateTime Built);

    /// <summary>Returns the newest engine source written after the running build compiled its assembly, in the checkout
    /// holding a document.</summary>
    /// <param name="documentPath">The path of a document in the checkout (the world's source).</param>
    /// <returns>The newest such source, or <see langword="null"/> when every source predates its assembly, or no checkout
    /// holds the document.</returns>
    public static StaleSource? NewestPastBuild(string documentPath) {
        if (CheckoutOf(path: documentPath) is not { } checkout) { return null; }
        StaleSource? newest = null;

        foreach (var assembly in Directory.EnumerateFiles(path: AppContext.BaseDirectory, searchPattern: "Puck*.dll")) {
            var project = Path.Combine(path1: checkout, path2: "src", path3: Path.GetFileNameWithoutExtension(path: assembly));

            if (!Directory.Exists(path: project)) { continue; }

            var built = File.GetLastWriteTimeUtc(path: assembly);

            foreach (var (path, written) in Sources(project: project)) {
                if ((written > built) && ((newest is null) || (written > newest.Value.Written))) {
                    newest = new StaleSource(Built: built, Path: PuckPaths.Relative(path: path, relativeTo: checkout), Written: written);
                }
            }
        }
        return newest;
    }

    /// <summary>Returns the sentence a refused reload appends when the running build predates the checkout's engine
    /// sources, naming the restart owed.</summary>
    /// <param name="documentPath">The path of the refused world's source.</param>
    /// <returns>The sentence, or <see langword="null"/> when the build is current or no checkout holds the
    /// source.</returns>
    public static string? RestartOwed(string documentPath) {
        try {
            return ((NewestPastBuild(documentPath: documentPath) is { } stale)
                ? $"restart owed: this World was built before {stale.Path} changed, so a document using what the newer engine adds is refused until the World restarts on a new build"
                : null);
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException)) {
            return null;
        }
    }

    // The checkout holding a path: the nearest directory at or above it with a Puck.slnx and a src directory.
    private static string? CheckoutOf(string path) {
        for (var directory = new DirectoryInfo(path: Path.GetFullPath(path: path)); (directory is not null); directory = directory.Parent) {
            if (File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx")) && Directory.Exists(path: Path.Combine(path1: directory.FullName, path2: "src"))) {
                return directory.FullName;
            }
        }
        return null;
    }
    // Every C# source and MSBuild file under a project directory, outside its build outputs, with its last write.
    private static FileSystemEnumerable<(string Path, DateTime Written)> Sources(string project) => new(
        directory: project,
        transform: static (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.LastWriteTimeUtc.UtcDateTime),
        options: new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true }
    ) {
        ShouldIncludePredicate = static (ref FileSystemEntry entry) => (!entry.IsDirectory &&
            (entry.FileName.EndsWith(value: ".cs", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            entry.FileName.EndsWith(value: ".csproj", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            entry.FileName.EndsWith(value: ".props", comparisonType: StringComparison.OrdinalIgnoreCase))),
        ShouldRecursePredicate = static (ref FileSystemEntry entry) => !(entry.FileName.Equals(other: "bin", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            entry.FileName.Equals(other: "obj", comparisonType: StringComparison.OrdinalIgnoreCase)),
    };
}
