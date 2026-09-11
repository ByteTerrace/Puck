using System.CommandLine;

using Puck.Cli.Scan.Analyzers;
using Puck.Cli.Source;

namespace Puck.Cli.Scan;

// The `puck scan` verb: parses every .cs file under a root ONCE and runs the selected analyzers over
// that single shared corpus. Returns 0, or 2 on an unknown analyzer or a missing root.
internal static class ScanCommand {
    // The analyzer registry in canonical order — the selection order, the -Only known set, and the error
    // text all read from it.
    private static readonly (string Name, Func<ISourceAnalyzer> Create)[] Analyzers = [
        ("comments", static () => new CommentAnalyzer()),
        ("comment-smells", static () => new CommentSmellAnalyzer()),
        ("locks", static () => new LockAnalyzer()),
        ("clones", static () => new CloneAnalyzer()),
    ];

    public static Command Create() {
        var groupedOption = new Option<bool>(name: "-Grouped", aliases: ["--grouped"]) { Description = "Additionally write <name>.grouped.json, the per-file work list." };
        var maxPerChunkOption = new Option<int>(name: "-MaxPerChunk", aliases: ["--max-per-chunk"]) { DefaultValueFactory = _ => ScanOptions.DefaultMaxPerChunk, Description = "Entries per grouped chunk." };
        var minStatementsOption = new Option<int>(name: "-MinStatements", aliases: ["--min-statements"]) { DefaultValueFactory = _ => ScanOptions.DefaultMinStatements, Description = "clones: minimum statement count." };
        var minTokensOption = new Option<int>(name: "-MinTokens", aliases: ["--min-tokens"]) { DefaultValueFactory = _ => ScanOptions.DefaultMinTokens, Description = "clones: minimum token count." };
        var noBlocksOption = new Option<bool>(name: "-NoBlocks", aliases: ["--no-blocks"]) { Description = "clones: skip the nested-block pass." };
        var onlyOption = new Option<string?>(name: "-Only", aliases: ["--only"]) { Description = $"Restrict to named analyzers, comma-separated (known: {KnownAnalyzers})." };
        var outDirOption = new Option<string?>(name: "-OutDir", aliases: ["--out-dir"]) { Description = "Write <name>.jsonl per analyzer here (default <repo>/artifacts/scan)." };
        var rootArgument = new Argument<string>(name: "root") { Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => "src", Description = "The tree to parse, resolved against the working directory." };
        var command = new Command(description: $"""
            Source sweep over the parsed tree: {KnownAnalyzers}.

            Artifact directories are pruned and the tree is parsed once for all selected analyzers.
            Records go to stdout when exactly one analyzer is selected and neither -OutDir nor -Grouped
            is given, and to files otherwise; the digest always goes to stderr. Output is deterministic.

              0  ran
              2  unknown analyzer, or a root that names nothing on disk
            """, name: "scan") {
            groupedOption,
            maxPerChunkOption,
            minStatementsOption,
            minTokensOption,
            noBlocksOption,
            onlyOption,
            outDirOption,
            rootArgument,
        };

        command.SetAction(action: parseResult => Run(
            grouped: parseResult.GetValue(option: groupedOption),
            maxPerChunk: parseResult.GetRequiredValue(option: maxPerChunkOption),
            minStatements: parseResult.GetRequiredValue(option: minStatementsOption),
            minTokens: parseResult.GetRequiredValue(option: minTokensOption),
            noBlocks: parseResult.GetValue(option: noBlocksOption),
            only: parseResult.GetValue(option: onlyOption),
            outDirectory: parseResult.GetValue(option: outDirOption),
            root: parseResult.GetRequiredValue(argument: rootArgument)));

        return command;
    }

    // The analyzer names in canonical order, for the -Only description and the unknown-name errors.
    private static string KnownAnalyzers =>
        string.Join(separator: ", ", values: Analyzers.Select(selector: static entry => entry.Name));

    private static int Run(bool grouped, int maxPerChunk, int minStatements, int minTokens, bool noBlocks, string? only, string? outDirectory, string root) {
        var selected = ResolveSelection(only: only);

        if (selected is null) {
            return 2;
        }

        // The repository root feeds exactly two defaults — the artifacts/scan output directory and the
        // shader-referent tree comment-smells resolves against. Resolve it only when one is live, so the
        // other analyzers run anywhere the rest of the verbs do (no Puck.slnx ancestor required).
        string? repositoryRoot = null;

        if (((outDirectory is null) || selected.Contains(item: "comment-smells"))
            && !CliPaths.TryGetRepositoryRoot(repositoryRoot: out repositoryRoot)) {
            return 2;
        }

        var options = ScanOptions.Create(
            grouped: grouped,
            maxPerChunk: maxPerChunk,
            minStatements: minStatements,
            minTokens: minTokens,
            noBlocks: noBlocks,
            outDirectory: outDirectory,
            repositoryRoot: repositoryRoot,
            singleStdout: ((selected.Count == 1) && (outDirectory is null) && !grouped));
        var corpus = SourceCorpus.TryLoad(rootArgument: root);

        if (corpus is null) {
            return 2;
        }

        foreach (var name in selected) {
            var analyzer = Analyzers.First(predicate: entry => string.Equals(a: entry.Name, b: name, comparisonType: StringComparison.Ordinal)).Create();

            var (jsonl, groupedRecords) = analyzer.Analyze(corpus: corpus, options: options);

            ScanSink.Emit(grouped: groupedRecords, jsonl: jsonl, name: name, options: options);
        }

        return 0;
    }
    // The requested analyzers in canonical order, all of them when -Only is absent, or null (with an
    // error already written) on an unknown name.
    private static List<string>? ResolveSelection(string? only) {
        var known = Analyzers.Select(selector: static entry => entry.Name).ToList();

        if (only is null) {
            return known;
        }

        var requested = only.Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: ',')
            .Select(selector: static name => name.ToLowerInvariant())
            .ToHashSet(comparer: StringComparer.Ordinal);

        // An empty or all-separator value selects nothing, and a run that produces no records at all is
        // indistinguishable from a clean tree. The caller meant something; say which spellings exist.
        if (requested.Count == 0) {
            Console.Error.WriteLine(value: $"ERROR: -Only named no scan analyzer (known: {KnownAnalyzers}).");

            return null;
        }

        foreach (var name in requested) {
            if (!known.Contains(item: name)) {
                Console.Error.WriteLine(value: $"ERROR: unknown scan analyzer '{name}' (known: {KnownAnalyzers}).");

                return null;
            }
        }

        return known.Where(predicate: requested.Contains).ToList();
    }
}
