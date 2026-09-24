using System.Diagnostics.CodeAnalysis;

namespace Puck.Cli;

// Path presentation for every verb: the relative, forward-slashed form that every printed path and every glob
// comparison uses, so output is stable regardless of where the tree sits, and the exit-2 form of repository-root
// discovery (scan anchors its artifact and shader-referent defaults there — arguments always resolve against the
// working directory). The walk itself is RepositoryPaths.Ascend.
internal static class CliPaths {
    private static readonly string? Root = RepositoryPaths.FindRoot();
    // No verb changes the working directory, so it is captured once: the display form is computed per
    // candidate file per glob during a walk and again per emitted record.
    private static readonly string WorkingDirectory = Directory.GetCurrentDirectory();

    // The form a path glob matches against (identical to ToDisplay; named for intent at the glob call sites).
    public static string RelForGlob(string fullPath) =>
        ToDisplay(fullPath: fullPath);
    // The form printed for a file addressed relative to the working directory — the default every verb's
    // reporting uses.
    public static string ToDisplay(string fullPath) =>
        ToDisplay(
            fullPath: fullPath,
            relativeTo: WorkingDirectory
        );
    // The form printed for a file addressed relative to an explicit base: the scan root for a corpus
    // entry.
    public static string ToDisplay(string relativeTo, string fullPath) =>
        Path.GetRelativePath(
            path: fullPath,
            relativeTo: relativeTo
        ).Replace(
            newChar: '/',
            oldChar: '\\'
        );
    // The repository root, or false with the failure already reported — scan turns that into exit 2
    // rather than an unhandled exception.
    public static bool TryGetRepositoryRoot([NotNullWhen(returnValue: true)] out string? repositoryRoot) {
        repositoryRoot = Root;

        if (repositoryRoot is null) {
            Console.Error.WriteLine(value: $"ERROR: could not locate the repository root (no Puck.slnx above {Environment.CurrentDirectory} or {AppContext.BaseDirectory}).");

            return false;
        }

        return true;
    }
}
