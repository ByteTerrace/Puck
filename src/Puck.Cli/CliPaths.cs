using System.Diagnostics.CodeAnalysis;

namespace Puck.Cli;

// Path resolution and presentation for every verb: repository-root discovery (scan anchors its artifact
// and shader-referent defaults there — arguments always resolve against the working directory), and the
// relative, forward-slashed form that every printed path and every glob comparison uses, so output is
// stable regardless of where the tree sits.
internal static class CliPaths {
    private static readonly string? Root = Discover();

    // No verb changes the working directory, so it is captured once: the display form is computed per
    // candidate file per glob during a walk and again per emitted record.
    private static readonly string WorkingDirectory = Directory.GetCurrentDirectory();

    // The repository root, or false with the failure already reported — scan turns that into exit 2
    // rather than an unhandled exception.
    public static bool TryGetRepositoryRoot([NotNullWhen(returnValue: true)] out string? repositoryRoot) {
        repositoryRoot = Root;

        if (repositoryRoot is null) {
            Console.Error.WriteLine(
                value: $"ERROR: could not locate the repository root (no Puck.slnx above {AppContext.BaseDirectory} or {Environment.CurrentDirectory}).");

            return false;
        }

        return true;
    }

    // The form printed for a file addressed relative to the working directory — the default every verb's
    // reporting uses.
    public static string ToDisplay(string fullPath) =>
        ToDisplay(relativeTo: WorkingDirectory, fullPath: fullPath);

    // The form printed for a file addressed relative to an explicit base: the scan root for a corpus
    // entry.
    public static string ToDisplay(string relativeTo, string fullPath) =>
        Path.GetRelativePath(relativeTo: relativeTo, path: fullPath).Replace(oldChar: '\\', newChar: '/');

    // The form a path glob matches against (identical to ToDisplay; named for intent at the glob call sites).
    public static string RelForGlob(string fullPath) =>
        ToDisplay(fullPath: fullPath);

    // The published executable lives inside the tree it operates on, so its own directory is the first
    // probe; the working directory covers an executable invoked from elsewhere in a checkout.
    private static string? Discover() =>
        (Ascend(start: AppContext.BaseDirectory) ?? Ascend(start: Environment.CurrentDirectory));
    private static string? Ascend(string start) {
        for (var directory = new DirectoryInfo(path: start); (directory is not null); directory = directory.Parent) {
            if (File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
                return directory.FullName;
            }
        }

        return null;
    }
}
