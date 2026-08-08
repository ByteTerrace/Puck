using Puck.Cli.Scan.Analyzers;
using Puck.Cli.Source;

namespace Puck.Cli.Scan;

// The `puck scan` verb: parses every .cs file under a root ONCE and runs the selected analyzers over
// that single shared corpus. Returns 0, or 2 on a usage error or a missing root.
internal static class ScanCommand {
    // The analyzer registry in canonical order — the selection order, the -Only known set, and the error
    // text all read from it.
    private static readonly (string Name, Func<ISourceAnalyzer> Create)[] Analyzers = [
        ("comments", static () => new CommentAnalyzer()),
        ("comment-smells", static () => new CommentSmellAnalyzer()),
        ("locks", static () => new LockAnalyzer()),
        ("clones", static () => new CloneAnalyzer()),
    ];

    public static int Run(string[] args) {
        var scanner = new ArgScanner()
            .Value(name: "Only").Value(name: "OutDir").Flag(name: "Grouped")
            .Value(name: "MaxPerChunk").Value(name: "MinTokens").Value(name: "MinStatements").Flag(name: "NoBlocks")
            .Flag(name: "h").Flag(name: "help");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"ERROR: {scanner.Error}");

            return 2;
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText());

            return 0;
        }

        var selected = ResolveSelection(only: scanner.Get(name: "Only"));

        if (selected is null) {
            return 2;
        }

        // The repository root feeds exactly two defaults — the artifacts/scan output directory and the
        // shader-referent tree comment-smells resolves against. Resolve it only when one is live, so the
        // other analyzers run anywhere the rest of the verbs do (no Puck.slnx ancestor required).
        var outDirArgument = scanner.Get(name: "OutDir");
        string? repositoryRoot = null;

        if (((outDirArgument is null) || selected.Contains(item: "comment-smells"))
            && !CliPaths.TryGetRepositoryRoot(repositoryRoot: out repositoryRoot)) {
            return 2;
        }

        var options = new ScanOptions {
            Grouped = scanner.Has(name: "Grouped"),
            IncludeBlocks = !scanner.Has(name: "NoBlocks"),
            MaxPerChunk = ((scanner.TryGetInt(name: "MaxPerChunk", value: out var maxPerChunk) && (maxPerChunk > 0)) ? maxPerChunk : 40),
            MinStatements = ((scanner.TryGetInt(name: "MinStatements", value: out var minStatements) && (minStatements > 0)) ? minStatements : 4),
            MinTokens = ((scanner.TryGetInt(name: "MinTokens", value: out var minTokens) && (minTokens > 0)) ? minTokens : 30),
            OutDirectory = ((outDirArgument is { } outDir)
                ? Path.GetFullPath(path: outDir)
                : Path.Combine(path1: repositoryRoot!, path2: "artifacts", path3: "scan")),
            ShaderRoot = ((repositoryRoot is null) ? string.Empty : Path.Combine(path1: repositoryRoot, path2: "src")),
            SingleStdout = ((selected.Count == 1) && !scanner.Has(name: "OutDir") && !scanner.Has(name: "Grouped")),
        };

        var root = ((scanner.Positionals.Count > 0) ? scanner.Positionals[0] : "src");
        var corpus = SourceCorpus.TryLoad(rootArgument: root);

        if (corpus is null) {
            return 2;
        }

        foreach (var name in selected) {
            var analyzer = Analyzers.First(predicate: entry => string.Equals(a: entry.Name, b: name, comparisonType: StringComparison.Ordinal)).Create();

            var (jsonl, grouped) = analyzer.Analyze(corpus: corpus, options: options);

            ScanSink.Emit(name: name, jsonl: jsonl, grouped: grouped, options: options);
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

        var requested = only.Split(separator: ',', options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(selector: static name => name.ToLowerInvariant())
            .ToHashSet(comparer: StringComparer.Ordinal);

        // An empty or all-separator value selects nothing, and a run that produces no records at all is
        // indistinguishable from a clean tree. The caller meant something; say which spellings exist.
        if (requested.Count == 0) {
            Console.Error.WriteLine(value: $"ERROR: -Only named no scan analyzer (known: {string.Join(separator: ", ", values: known)}).");

            return null;
        }

        foreach (var name in requested) {
            if (!known.Contains(item: name)) {
                Console.Error.WriteLine(value: $"ERROR: unknown scan analyzer '{name}' (known: {string.Join(separator: ", ", values: known)}).");

                return null;
            }
        }

        return known.Where(predicate: requested.Contains).ToList();
    }

    // The synopsis, with the analyzer registry read from its single declaration site. What each analyzer
    // emits, and the record shapes, are the README's job; this exists so every verb answers -h.
    private static string HelpText() =>
        $"""
        scan [<root=src>]   source sweep over the parsed tree

          -Only <a,a>       restrict to named analyzers
          -OutDir <dir>     write <name>.jsonl per analyzer here (default <repo>/artifacts/scan)
          -Grouped          additionally write <name>.grouped.json, the per-file work list
          -MaxPerChunk <n>  entries per grouped chunk (default 40)
          -MinTokens <n>    clones: minimum token count (default 30)
          -MinStatements <n> clones: minimum statement count (default 4)
          -NoBlocks         clones: skip the nested-block pass
          -h / --help       this text

        Analyzers: {string.Join(separator: ", ", values: Analyzers.Select(selector: static entry => entry.Name))}

        <root> resolves against the working directory, the same rule every verb
        applies; artifact directories are pruned and the tree is parsed
        once for all selected analyzers. Records go to stdout when exactly one
        analyzer is selected and neither -OutDir nor -Grouped is given, and to files
        otherwise; the digest always goes to stderr. Output is deterministic.
        Exit codes: 0 ran, 2 usage error, unknown analyzer, or missing root.
        """;
}
