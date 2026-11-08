using System.Diagnostics.CodeAnalysis;

namespace Puck.Cli;

// Path presentation for every verb: contained paths are relative, outside paths absolute, all forward-slashed.
// Glob matching keeps working-directory-relative paths. Also owns the exit-2 form of repository-root
// discovery (scan anchors its artifact and shader-referent defaults there — arguments always resolve against the
// working directory). The walk itself is RepositoryPaths.Ascend.
public static class CliPaths {
    private static readonly string? Root = RepositoryPaths.FindRoot();
    // No verb changes the working directory, so it is captured once: the path form is computed per
    // candidate file per glob during a walk and again per emitted record.
    private static readonly string WorkingDirectory = Directory.GetCurrentDirectory();

    /// <summary>The file name of the <c>puck</c> root's assembly, which every verb assembly's output sits beside.</summary>
    public const string ToolAssembly = "Puck.Cli.dll";

    /// <summary>The <c>puck</c> root this process runs from, for a verb that starts another verb in a process of its own:
    /// <see cref="ToolAssembly"/> in the application's base directory.</summary>
    public static string Tool => Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: ToolAssembly
    );

    // Path globs match relative paths even outside the working directory.
    public static string RelForGlob(string fullPath) =>
        Path.GetRelativePath(
            path: fullPath,
            relativeTo: WorkingDirectory
        ).Replace(
            newChar: '/',
            oldChar: '\\'
        );
    /// <summary>Returns whether two paths name the same file or directory: compared in full, without a trailing separator,
    /// and without case on Windows.</summary>
    /// <param name="left">One path, absolute or relative to the working directory.</param>
    /// <param name="right">The other path.</param>
    /// <returns><see langword="true"/> when both resolve to the same full path.</returns>
    public static bool Same(string left, string right) => string.Equals(
        a: Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: left)),
        b: Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: right)),
        comparisonType: (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
    );
    // The default display base is the working directory.
    public static string ToDisplay(string fullPath) =>
        ToDisplay(
            fullPath: fullPath,
            relativeTo: WorkingDirectory
        );
    // A path at or beneath the explicit base is relative; every other path is absolute.
    public static string ToDisplay(string relativeTo, string fullPath) {
        relativeTo = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: relativeTo));
        fullPath = Path.GetFullPath(path: fullPath);
        var contained = Puck.Abstractions.PuckPaths.IsWithin(path: fullPath, root: relativeTo);

        return (contained
            ? Path.GetRelativePath(path: fullPath, relativeTo: relativeTo)
            : fullPath
        ).Replace(
            newChar: '/',
            oldChar: '\\'
        );
    }
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
