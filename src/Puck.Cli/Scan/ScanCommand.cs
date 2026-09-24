using System.CommandLine;

using Puck.Cli.Scan.Analyzers;
using Puck.Cli.Source;

namespace Puck.Cli.Scan;

// The `puck scan` verb: parses every .cs file under a root ONCE and runs the selected analyzers over
// that single shared corpus.
internal static class ScanCommand {
    // The analyzer registry in canonical order — the selection order, the --only known set, and the error
    // text all read from it.
    private static readonly (string Name, Func<ISourceAnalyzer> Create)[] Analyzers = [
        ("comments", static () => new CommentAnalyzer()),
        ("comment-smells", static () => new CommentSmellScan()),
        ("locks", static () => new LockAnalyzer()),
        ("clones", static () => new CloneAnalyzer()),
    ];

    // The requested analyzers in canonical order, all of them when --only is absent, or null (with an
    // error already written) on an unknown name.
    private static List<string>? ResolveSelection(string? only) {
        var known = Analyzers.Select(selector: static entry => entry.Name).ToList();

        if (only is null) {
            return known;
        }

        var requested = only.Split(
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
            separator: ','
        )
            .Select(selector: static name => name.ToLowerInvariant())
            .ToHashSet(comparer: StringComparer.Ordinal);

        // An empty or all-separator value selects nothing, and a run that produces no records at all is
        // indistinguishable from a clean tree. The caller meant something; say which spellings exist.
        if (requested.Count == 0) {
            _ = CliExit.Refuse(
                verb: "scan",
                what: "--only",
                why: $"names no analyzer (known: {KnownAnalyzers})."
            );

            return null;
        }

        foreach (var name in requested) {
            if (!known.Contains(item: name)) {
                _ = CliExit.Refuse(
                    verb: "scan",
                    what: $"--only {name}",
                    why: $"no such analyzer (known: {KnownAnalyzers})."
                );

                return null;
            }
        }

        return known.Where(predicate: requested.Contains).ToList();
    }
    private static int Run(bool grouped, int maxPerChunk, int minStatements, int minTokens, bool noBlocks, string? only, string? outDirectory, string root) {
        var selected = ResolveSelection(only: only);

        if (selected is null) {
            return CliExit.Refused;
        }

        // The repository root feeds exactly two defaults — the artifacts/scan output directory and the
        // shader-referent tree comment-smells resolves against. Resolve it only when one is live, so the
        // other analyzers run anywhere the rest of the verbs do (no Puck.slnx ancestor required).
        string? repositoryRoot = null;

        if (
            ((outDirectory is null) || selected.Contains(item: "comment-smells")) &&
            !CliPaths.TryGetRepositoryRoot(repositoryRoot: out repositoryRoot)
        ) {
            return CliExit.Refused;
        }

        var options = ScanOptions.Create(
            grouped: grouped,
            maxPerChunk: maxPerChunk,
            minStatements: minStatements,
            minTokens: minTokens,
            noBlocks: noBlocks,
            outDirectory: outDirectory,
            repositoryRoot: repositoryRoot,
            singleStdout: ((selected.Count == 1) && (outDirectory is null) && !grouped)
        );
        var corpus = SourceCorpus.TryLoad(rootArgument: root);

        if (corpus is null) {
            return CliExit.Refused;
        }

        foreach (var name in selected) {
            var analyzer = Analyzers.First(predicate: entry => string.Equals(
                a: entry.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )).Create();

            var (jsonl, groupedRecords) = analyzer.Analyze(
                corpus: corpus,
                options: options
            );

            ScanSink.Emit(
                grouped: groupedRecords,
                jsonl: jsonl,
                name: name,
                options: options
            );
        }

        return 0;
    }

    public static Command Create() {
        var groupedOption = new Option<bool>(name: "--grouped") { Description = "Also write <name>.grouped.json, the per-file work list." };
        var maxPerChunkOption = new Option<int>(name: "--max-per-chunk") { DefaultValueFactory = _ => ScanOptions.DefaultMaxPerChunk, Description = "Entries per grouped chunk." };
        var minStatementsOption = new Option<int>(name: "--min-statements") { DefaultValueFactory = _ => ScanOptions.DefaultMinStatements, Description = "clones: minimum statement count." };
        var minTokensOption = new Option<int>(name: "--min-tokens") { DefaultValueFactory = _ => ScanOptions.DefaultMinTokens, Description = "clones: minimum token count." };
        var noBlocksOption = new Option<bool>(name: "--no-blocks") { Description = "clones: skip the nested-block pass." };
        var onlyOption = new Option<string?>(name: "--only") { Description = $"Restrict to named analyzers, comma-separated (known: {KnownAnalyzers})." };
        var outputOption = CliOptions.Output(description: "The directory each analyzer's <name>.jsonl is written to (default <repo>/artifacts/scan).");
        var rootArgument = new Argument<string>(name: "root") { Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => "src", Description = "The tree to parse, resolved against the working directory." };
        var command = new Command(
            description: "Sweep the parsed C# tree for comments, comment smells, locks, and clones.",
            name: "scan"
        ) {
            groupedOption,
            maxPerChunkOption,
            minStatementsOption,
            minTokensOption,
            noBlocksOption,
            onlyOption,
            outputOption,
            rootArgument,
        };

        command.Detail(detail: $"""
            Analyzers: {KnownAnalyzers}. The tree is parsed once for every selected analyzer, and
            the walk skips {FileWalk.PrunedNames}, and agent worktrees under .claude/worktrees.

            Records go to stdout when exactly one analyzer is selected and neither --output nor
            --grouped is given, and to files otherwise; the digest always goes to stderr. Output
            is deterministic.

            Exit codes: 0 ran; 2 an unknown analyzer, or a root that names nothing on disk.
            """);
        command.SetAction(action: parseResult => Run(
            grouped: parseResult.GetValue(option: groupedOption),
            maxPerChunk: parseResult.GetRequiredValue(option: maxPerChunkOption),
            minStatements: parseResult.GetRequiredValue(option: minStatementsOption),
            minTokens: parseResult.GetRequiredValue(option: minTokensOption),
            noBlocks: parseResult.GetValue(option: noBlocksOption),
            only: parseResult.GetValue(option: onlyOption),
            outDirectory: parseResult.GetValue(option: outputOption),
            root: parseResult.GetRequiredValue(argument: rootArgument)
        ));

        return command;
    }

    // The analyzer names in canonical order, for the --only description and the unknown-name errors.
    private static string KnownAnalyzers =>
        string.Join(
            separator: ", ",
            values: Analyzers.Select(selector: static entry => entry.Name)
        );
}
