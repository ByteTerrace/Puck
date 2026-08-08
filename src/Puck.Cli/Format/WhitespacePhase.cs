using Puck.Cli.Source;

namespace Puck.Cli.Format;

// Phase 0 of `format`: the SDK's whitespace formatter applies the .editorconfig baseline the custom
// passes build on. Run per owning project — projects outside the solution would be missed by one
// solution-wide invocation — with --no-restore (the project must already be restored/built, same as
// named-args). In verify mode the formatter's "changes needed" (nonzero) maps to a drift/gate failure
// (1); a genuine tool error in write mode maps to infra (2).
internal static class WhitespacePhase {
    public static int Run(string rootArgument, bool verifyOnly) {
        if (!SourceFiles.TryEnumerate(rootArgument: rootArgument, scanRoot: out _, files: out var files)) {
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

        var result = 0;

        foreach (var project in projects) {
            Console.Error.WriteLine(value: $"dotnet format whitespace: {CliPaths.ToDisplay(fullPath: project)}");

            var code = (verifyOnly
                ? CliProcess.RunStreamed(fileName: "dotnet", "format", "whitespace", project, "--no-restore", "--verify-no-changes")
                : CliProcess.RunStreamed(fileName: "dotnet", "format", "whitespace", project, "--no-restore"));

            if (code != 0) {
                result = Math.Max(val1: result, val2: (verifyOnly ? 1 : 2));
            }
        }

        return result;
    }
}
