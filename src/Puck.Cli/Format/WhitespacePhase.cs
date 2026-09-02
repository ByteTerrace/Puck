using Puck.Cli.Source;

namespace Puck.Cli.Format;

// Phase 0 of `format`: the SDK's whitespace formatter applies the .editorconfig baseline the custom
// passes build on. Run per owning project — projects outside the solution would be missed by one
// solution-wide invocation — with --no-restore (the project must already be restored/built, same as
// named-args) and scoped to the requested root, because a project formats every COMPILE ITEM it carries and
// some of those are linked in from outside it (build/VerifiedCodeAttribute.cs is linked into every project,
// so `format src/Puck.Commands` rewrote a file two directories above the root it was handed). In verify mode
// the formatter's "changes needed" (nonzero) maps to a drift/gate failure (1); a genuine tool error in write
// mode maps to infra (2).
internal static class WhitespacePhase {
    // The --include pattern that confines a run to <root>, or null when the root cannot be spelled as one.
    // `dotnet format` matches --include against each document's path RELATIVE TO THE WORKING DIRECTORY, and reads a
    // pattern as a directory only when it ends in a separator: an absolute path, and a directory named without the
    // trailing slash, each match NOTHING — silently, and the run then reports a clean tree it never looked at. That
    // failure mode is why a root outside the working directory answers null (phase 0 runs unscoped and says so)
    // rather than a pattern with a `..` segment the glob matcher would quietly drop everything for.
    internal static string? IncludePattern(string scanRoot, string workingDirectory) {
        var relative = Path.GetRelativePath(
            path: scanRoot,
            relativeTo: workingDirectory
        ).Replace(
            newChar: '/',
            oldChar: '\\'
        );

        if (
            Path.IsPathRooted(path: relative) ||
            (relative == "..") ||
            relative.StartsWith(value: "../", comparisonType: StringComparison.Ordinal)
        ) {
            return null;
        }

        return (relative.EndsWith(value: '/')
            ? relative
            : $"{relative}/");
    }

    public static int Run(string rootArgument, bool verifyOnly) {
        if (!SourceFiles.TryEnumerate(files: out var files, rootArgument: rootArgument, scanRoot: out var scanRoot)) {
            return 2;
        }

        // Phase 0 formats exactly the projects that OWN corpus files, so the corpus pruning (agent
        // worktrees, artifact directories) governs here too — a raw *.csproj walk under the root would
        // reach checkouts the corpus refuses and, in write mode, rewrite another session's files. A file
        // with no owning project contributes no project (named-args reports those when it runs).
        var owningDirectories = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var projects = new SortedSet<string>(comparer: StringComparer.Ordinal);

        foreach (var file in files) {
            var directory = SourceFiles.FindOwningProjectDirectory(start: Path.GetDirectoryName(path: file)!);

            if ((directory is null) || !owningDirectories.Add(item: directory)) {
                continue;
            }

            foreach (var project in Directory.EnumerateFiles(path: directory, searchPattern: "*.csproj")) {
                projects.Add(item: project);
            }
        }

        var include = IncludePattern(
            scanRoot: scanRoot,
            workingDirectory: Environment.CurrentDirectory
        );
        var result = 0;

        if ((include is null) && (projects.Count > 0)) {
            Console.Error.WriteLine(value: $"dotnet format whitespace: {CliPaths.ToDisplay(fullPath: scanRoot)} does not sit under the working directory — phase 0 runs UNSCOPED and may format compile items linked in from outside it.");
        }

        foreach (var project in projects) {
            Console.Error.WriteLine(value: $"dotnet format whitespace: {CliPaths.ToDisplay(fullPath: project)}");

            var code = (include is null
                ? (verifyOnly
                    ? CliProcess.RunStreamed(fileName: "dotnet", "format", "whitespace", project, "--no-restore", "--verify-no-changes")
                    : CliProcess.RunStreamed(fileName: "dotnet", "format", "whitespace", project, "--no-restore"))
                : (verifyOnly
                    ? CliProcess.RunStreamed(fileName: "dotnet", "format", "whitespace", project, "--no-restore", "--verify-no-changes", "--include", include)
                    : CliProcess.RunStreamed(fileName: "dotnet", "format", "whitespace", project, "--no-restore", "--include", include)));

            if (code != 0) {
                result = Math.Max(val1: result, val2: (verifyOnly ? 1 : 2));
            }
        }

        return result;
    }
}
