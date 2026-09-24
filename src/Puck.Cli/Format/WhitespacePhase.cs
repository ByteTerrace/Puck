using Puck.Cli.Source;

namespace Puck.Cli.Format;

// Phase 0 of `format`: the SDK's whitespace formatter applies the .editorconfig baseline the custom passes build on.
// Whitespace formatting reads syntax and the .editorconfig chain above each file, never a project's semantics, so the
// formatter runs in folder mode over exactly the files it is handed: no project or its reference graph is loaded or
// restored, and a compile item a project links in from outside the root is never reached, because only listed files
// are. Folder mode defines no preprocessor symbols, so a region under `#if SYMBOL` is inactive and left as written.
//
// The file list reaches the formatter as `--include` arguments, split into as few deterministic batches as a command
// line holds. A response file does not lift the limit: `dotnet format` is the SDK host forwarding to a second process,
// and the host expands `@file` onto that process's command line. With --check any batch's "changes needed" (nonzero)
// maps to a drift/gate failure (1); a genuine tool error in write mode maps to infra (2).
internal static class WhitespacePhase {
    // The characters of `--include` paths one invocation may carry. Windows caps a whole command line at 32767
    // characters; the rest is headroom for what precedes the paths on the forwarded line — the dotnet host's path,
    // `exec`, the formatter assembly's path under the SDK, and the verb's own switches.
    internal const int CommandLineBudget = 24_576;

    // Each path as `--include` matches it: forward-slashed and relative to the workspace folder.
    internal static string[] Includes(string workspace, IEnumerable<string> files) =>
        [.. files.Select(selector: file => Path.GetRelativePath(
            path: file,
            relativeTo: workspace
        ).Replace(
            newChar: '/',
            oldChar: '\\'
        ))];
    // Splits `includes` into consecutive batches, in order, each costing at most `budget`. A path costs its length plus
    // three: the separating space and the quotes a path with a space is wrapped in. A path that alone exceeds the budget
    // travels alone rather than being dropped.
    internal static List<string[]> Batches(IReadOnlyList<string> includes, int budget = CommandLineBudget) {
        var batches = new List<string[]>();
        var start = 0;
        var cost = 0;

        for (var index = 0; (index < includes.Count); index++) {
            var next = (includes[index].Length + 3);

            if (
                (index > start) &&
                ((cost + next) > budget)
            ) {
                batches.Add(item: [.. includes.Skip(count: start).Take(count: (index - start))]);
                start = index;
                cost = 0;
            }

            cost += next;
        }

        if (start < includes.Count) {
            batches.Add(item: [.. includes.Skip(count: start)]);
        }

        return batches;
    }

    public static int Run(string rootArgument, bool check, string[]? targets = null) {
        var files = targets;
        var workspace = ((targets is null)
            ? string.Empty
            : Path.GetFullPath(path: rootArgument)
        );

        if (
            (files is null) &&
            !SourceFiles.TryEnumerate(
            files: out files,
            rootArgument: rootArgument,
            scanRoot: out workspace,
            verb: "format"
        )
        ) {
            return 2;
        }

        // An empty --include list is no restriction at all: the folder workspace would take every file under it.
        if (files.Length == 0) {
            return 0;
        }

        var batches = Batches(includes: Includes(
            files: files,
            workspace: workspace
        ));

        Console.Error.WriteLine(value: $"dotnet format whitespace: {files.Length} file(s) under {CliPaths.ToDisplay(fullPath: workspace)} in {batches.Count} invocation(s)");

        var result = 0;

        // Every batch runs, so a check names every drifted file rather than the first batch's.
        foreach (var batch in batches) {
            string[] arguments = (check
                ? ["format", "whitespace", ".", "--folder", "--verify-no-changes", "--include", .. batch]
                : ["format", "whitespace", ".", "--folder", "--include", .. batch]);
            var code = CliProcess.RunAsync(
                arguments: arguments,
                capture: false,
                fileName: "dotnet",
                workingDirectory: workspace
            ).GetAwaiter().GetResult().ExitCode;

            if (code != 0) {
                result = (check
                    ? 1
                    : 2);
            }
        }

        return result;
    }
}
