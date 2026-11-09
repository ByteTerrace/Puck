using System.CommandLine;

using Puck.Abstractions;
using Puck.Cli.Scan.Analyzers;
using Puck.Cli.Source;

namespace Puck.Cli.Scan;

// The `puck scan` verb: parses every .cs file under a root ONCE and runs the selected analyzers over
// that single shared corpus.
public static class ScanCommand {
    // The analyzer registry in canonical order — the selection order, the --only known set, and the error
    // text all read from it.
    private static readonly (string Name, Func<ISourceAnalyzer> Create)[] Analyzers = [
        ("comments", static () => new CommentAnalyzer()),
        ("comment-smells", static () => new CommentSmellScan()),
        ("locks", static () => new LockAnalyzer()),
        ("clones", static () => new CloneAnalyzer()),
        ("identity-counters", static () => new IdentityCounterAnalyzer()),
        ("exact-keys", static () => new ExactKeyAnalyzer()),
        (BranchAnalyzer, static () => new DuplicateAnalyzer()),
    ];
    // The analyzer that judges what a branch wrote: it runs only against a base, so a sweep without --against leaves it out
    // and --only naming it without --against is refused.
    private const string BranchAnalyzer = "duplicates";

    // The requested analyzers in canonical order, all of them when --only is absent (the branch analyzer only when a base is
    // given), or null (with an error already written) on an unknown name.
    private static List<string>? ResolveSelection(string? only, bool branchScoped) {
        var known = Analyzers.Select(selector: static entry => entry.Name).ToList();

        if (only is null) {
            return (branchScoped
                ? known
                : known.Where(predicate: static name => (name != BranchAnalyzer)).ToList());
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
    // The lines the branch added against `against`, and where its files are read from: the working tree, or an export of
    // `head` when one is given (so a past branch is scanned as it was, not as the tree has moved on). Null, with the refusal
    // written, when git cannot answer.
    private static BranchScope? ReadBranch(string against, string? head, string repositoryRoot, string root, out RevisionExport? export, out string corpusRoot) {
        export = null;
        corpusRoot = root;

        var scanRoot = Path.GetFullPath(path: root);

        if (!PuckPaths.IsWithin(path: scanRoot, root: repositoryRoot)) {
            _ = CliExit.Refuse(verb: "scan", what: "--against", why: $"the scan root {CliPaths.ToDisplay(fullPath: scanRoot)} is outside the repository, so no diff names its files.");

            return null;
        }

        var relative = Path.GetRelativePath(path: scanRoot, relativeTo: repositoryRoot).Replace(newChar: '/', oldChar: '\\');
        var baseCommit = string.Empty;
        var headCommit = string.Empty;

        if (!CliGit.TryResolveCommit(repository: repositoryRoot, resolved: out baseCommit, revision: against)) {
            _ = CliExit.Refuse(verb: "scan", what: $"--against {against}", why: "not a commit this repository carries.");

            return null;
        }

        if ((head is not null) && !CliGit.TryResolveCommit(repository: repositoryRoot, resolved: out headCommit, revision: head)) {
            _ = CliExit.Refuse(verb: "scan", what: $"--head {head}", why: "not a commit this repository carries.");

            return null;
        }

        if (!CliGitDiff.TryReadAdded(
            added: out var added,
            baseRevision: baseCommit,
            head: ((head is null) ? null : headCommit),
            pathspec: "*.cs",
            reason: out var reason,
            repository: repositoryRoot
        )) {
            _ = CliExit.Refuse(verb: "scan", what: $"--against {against}", why: reason);

            return null;
        }

        if (head is not null) {
            try {
                export = RevisionExport.Create(
                    paths: [relative],
                    repository: repositoryRoot,
                    revision: headCommit
                );
            } catch (RevisionExportRefusedException exception) {
                _ = CliExit.Refuse(verb: "scan", what: $"--head {head}", why: exception.Message);

                return null;
            }

            corpusRoot = Path.Combine(
                path1: export.Root,
                path2: relative
            );
        }

        return new BranchScope(
            Added: added,
            Against: baseCommit[..12],
            Head: ((head is null) ? "the working tree" : headCommit[..12]),
            PathPrefix: ((relative == ".") ? string.Empty : $"{relative}/")
        );
    }
    private static int Run(string? against, bool grouped, string? head, int maxPerChunk, int minExpressionTokens, int minStatements, int minTokens, bool noBlocks, string? only, string? outDirectory, string root) {
        if ((head is not null) && (against is null)) {
            _ = CliExit.Refuse(verb: "scan", what: "--head", why: "names the end of a branch, which needs --against, its base.");

            return CliExit.Refused;
        }

        var selected = ResolveSelection(
            branchScoped: (against is not null),
            only: only
        );

        if (selected is null) {
            return CliExit.Refused;
        }

        if ((against is null) && selected.Contains(item: BranchAnalyzer)) {
            _ = CliExit.Refuse(verb: "scan", what: $"--only {BranchAnalyzer}", why: "judges what a branch wrote, which needs --against <ref>, the base it is measured from.");

            return CliExit.Refused;
        }

        // The repository root feeds three things — the artifacts/scan output directory, the shader-referent tree
        // comment-smells resolves against, and the branch scope's diff. Resolve it only when one is live, so the
        // other analyzers run anywhere the rest of the verbs do (no Puck.slnx ancestor required).
        string? repositoryRoot = null;

        if (
            ((outDirectory is null) || selected.Contains(item: "comment-smells") || (against is not null)) &&
            !CliPaths.TryGetRepositoryRoot(repositoryRoot: out repositoryRoot)
        ) {
            return CliExit.Refused;
        }

        RevisionExport? export = null;
        BranchScope? branch = null;
        var corpusRoot = root;

        if (against is not null) {
            branch = ReadBranch(
                against: against,
                corpusRoot: out corpusRoot,
                export: out export,
                head: head,
                repositoryRoot: repositoryRoot!,
                root: root
            );

            if (branch is null) {
                return CliExit.Refused;
            }
        }

        using var exported = export;

        var options = ScanOptions.Create(
            branch: branch,
            grouped: grouped,
            maxPerChunk: maxPerChunk,
            minExpressionTokens: minExpressionTokens,
            minStatements: minStatements,
            minTokens: minTokens,
            noBlocks: noBlocks,
            outDirectory: outDirectory,
            repositoryRoot: repositoryRoot,
            singleStdout: ((selected.Count == 1) && (outDirectory is null) && !grouped)
        );
        var corpus = SourceCorpus.TryLoad(rootArgument: corpusRoot);

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
        var againstOption = new Option<string?>(name: "--against") { Description = "duplicates: the revision a branch is measured from; the types and methods the branch adds or changes since its merge base with --head are checked for near neighbours elsewhere in the tree." };
        var groupedOption = new Option<bool>(name: "--grouped") { Description = "Also write <name>.grouped.json, the per-file work list." };
        var headOption = new Option<string?>(name: "--head") { Description = "duplicates: the revision holding the branch's work, scanned as it was there (default: the working tree, uncommitted and untracked files included)." };
        var maxPerChunkOption = CliOptions.Count(defaultValue: ScanOptions.DefaultMaxPerChunk, description: "Entries per grouped chunk.", minimum: 1, name: "--max-per-chunk");
        var minExpressionTokensOption = CliOptions.Count(defaultValue: ScanOptions.DefaultMinExpressionTokens, description: "clones: minimum token count of a cloned expression; 0 skips the expression pass.", minimum: 0, name: "--min-expression-tokens");
        var minStatementsOption = CliOptions.Count(defaultValue: ScanOptions.DefaultMinStatements, description: "clones: minimum statement count.", minimum: 1, name: "--min-statements");
        var minTokensOption = CliOptions.Count(defaultValue: ScanOptions.DefaultMinTokens, description: "clones: minimum token count.", minimum: 1, name: "--min-tokens");
        var noBlocksOption = new Option<bool>(name: "--no-blocks") { Description = "clones: skip the nested-block pass." };
        var onlyOption = new Option<string?>(name: "--only") { Description = $"Restrict to named analyzers, comma-separated (known: {KnownAnalyzers})." };
        var outputOption = CliOptions.Output(description: "The directory each analyzer's <name>.jsonl is written to (default <repo>/artifacts/scan).");
        var rootArgument = new Argument<string>(name: "root") { Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => "src", Description = "The tree to parse, resolved against the working directory." };
        var command = new Command(
            description: "Sweep the parsed C# tree for comments, comment smells, locks, clones, identity counters, exact cache keys, and (against a base) a branch's duplicate techniques.",
            name: "scan"
        ) {
            againstOption,
            groupedOption,
            headOption,
            maxPerChunkOption,
            minExpressionTokensOption,
            minStatementsOption,
            minTokensOption,
            noBlocksOption,
            onlyOption,
            outputOption,
            rootArgument,
        };

        command.Detail(detail: $"""
            Analyzers: {KnownAnalyzers}. The tree is parsed once for every selected analyzer.
            {FileWalk.WalkSummary}

            Records go to stdout when exactly one analyzer is selected and neither --output nor
            --grouped is given, and to files otherwise; the digest always goes to stderr. Output
            is deterministic.

            duplicates is the branch check, run by a lane before it hands back and by the lead
            before it lands one: `puck scan --only duplicates --against <integration-ref>`. It lists
            the types and methods the branch adds or changes, each with the declarations elsewhere
            in the tree that carry the same technique under another name (shared constants, BCL
            markers such as Math.BigMul or Interlocked, and the shape of the body with identifiers
            abstracted), ranked, and flags private or internal helpers shaped like a general
            technique (a divisor, hash, comparer, pool, retry or cache) with the shared home to
            search. A hit is a lead to read, not a verdict. Without --head the working tree is
            scanned; with it, an export of that revision is, so a past range replays as it was.
            With --against and no --only, duplicates runs with the rest of the sweep.

            Exit codes: 0 ran; 2 an unknown analyzer, an unknown revision, a root that names
            nothing on disk, or --head or the duplicates analyzer without --against.
            """);
        command.SetAction(action: parseResult => Run(
            against: parseResult.GetValue(option: againstOption),
            grouped: parseResult.GetValue(option: groupedOption),
            head: parseResult.GetValue(option: headOption),
            maxPerChunk: parseResult.GetRequiredValue(option: maxPerChunkOption),
            minExpressionTokens: parseResult.GetRequiredValue(option: minExpressionTokensOption),
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
