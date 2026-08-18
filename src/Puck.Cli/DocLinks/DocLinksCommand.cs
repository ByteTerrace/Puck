using System.Text.RegularExpressions;

namespace Puck.Cli.DocLinks;

/// <summary><c>puck doc-links</c> — checks every relative markdown link and cited repository path in a fixed
/// documentation set resolves. For each document it extracts: (a) every relative markdown link target
/// (<c>[text](path)</c>) — external schemes and pure in-page anchors are skipped, a fragment is stripped —
/// resolved against the document's own directory, then the repository root; (b) every backticked rooted
/// repository path (<c>src/...</c>, <c>docs/...</c>, <c>tests/...</c>, <c>build/...</c>) with a file extension,
/// resolved the same way; (c) every backticked bare filename with a source-ish extension, looked up in an index
/// of every filename under <c>src/</c>, <c>docs/</c>, <c>tests/</c>, <c>build/</c>, and <c>.claude/skills/</c> —
/// enforced for documents under <c>src/</c> (a project README cites its neighbors), advisory for documents under
/// <c>docs/</c>, which legitimately name out-of-repo files. One control — a deliberately nonexistent path must
/// fail resolution — runs before any document, so a green run proves the checker can turn red.</summary>
internal static class DocLinksCommand {
    private static readonly string[] DefaultDocuments = [
        "src/Puck.World/README.md",
        "src/Puck.World.Client/README.md",
        "src/Puck.World/Audio/README.md",
        "src/Puck.World.Schema/README.md",
        "src/Puck.World.Server/README.md",
        "src/Puck.Attestation/README.md",
        "README.md",
        "docs/agent-guide.md",
        "docs/project-map.md",
        "docs/campaign.md",
        "docs/vision.md",
    ];
    // CLAUDE.md rule 1 pins these paths as existing only in git history; docs/project-map.md states exactly
    // that where it names them, so their non-resolution is correct, not a broken citation.
    private static readonly string[] HistoricalCitations = ["src/Puck", "src/Puck.Avatars"];
    // A document superseded and deleted, cited only as the thing a living document replaced.
    private static readonly string[] HistoricalFileNames = ["addon-input-plan.md"];
    private static readonly string[] IndexedTrees = ["src", "docs", "tests", "build", ".claude/skills"];
    private static readonly Regex MarkdownLink = new(options: RegexOptions.Compiled, pattern: @"\[[^\]]*\]\(([^)\s]+)\)");
    private static readonly Regex Backticked = new(options: RegexOptions.Compiled, pattern: "`([^`]+)`");
    private static readonly Regex RootedPath = new(options: RegexOptions.Compiled, pattern: @"^(src|docs|tests|build|\.claude)/[A-Za-z0-9._/\-]+\.[A-Za-z0-9]+$");
    private static readonly Regex BareFileName = new(options: RegexOptions.Compiled, pattern: @"^[A-Za-z0-9._\-]+\.(cs|md|json|ps1|csproj|props|slnx|wasm|wat|py)$");
    private static readonly Regex ExternalScheme = new(options: RegexOptions.Compiled | RegexOptions.IgnoreCase, pattern: @"^[a-z][a-z0-9+.\-]*:");

    public static int Run(string[] args) {
        if ((Array.IndexOf(array: args, value: "-h") >= 0) || (Array.IndexOf(array: args, value: "--help") >= 0)) {
            return Usage();
        }
        if ((args.Length != 0) && (args[0] == "--")) {
            args = args[1..];
        }
        if (args.Any(predicate: static argument => argument.StartsWith(value: '-'))) {
            Console.Error.WriteLine(value: "ERROR: the only accepted form is: doc-links [<document> ...]");

            return 2;
        }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        var documents = ((args.Length == 0) ? DefaultDocuments : args);
        var fileNameIndex = BuildFileNameIndex(repositoryRoot: repositoryRoot);

        if (TryResolve(documentDirectory: repositoryRoot, repositoryRoot: repositoryRoot, target: "src/Puck.World/this-file-does-not-exist.md")
            || fileNameIndex.Contains(item: "this-file-does-not-exist.md")) {
            Console.Error.WriteLine(value: "ERROR: CONTROL FAILED: a nonexistent path resolved — the checker cannot discriminate.");

            return 2;
        }

        var failures = new List<string>();
        var advisories = new List<string>();
        var checkedCount = 0;

        foreach (var document in documents) {
            var documentPath = Path.Combine(path1: repositoryRoot, path2: document);

            if (!File.Exists(path: documentPath)) {
                failures.Add(item: $"{document}: the document itself does not exist");

                continue;
            }

            var documentDirectory = (Path.GetDirectoryName(path: documentPath) ?? repositoryRoot);
            var enforceBareFileNames = document.Replace(newChar: '/', oldChar: '\\').StartsWith(comparisonType: StringComparison.Ordinal, value: "src/");
            var lines = File.ReadAllLines(path: documentPath);

            for (var lineNumber = 1; (lineNumber <= lines.Length); lineNumber++) {
                foreach (var citation in Citations(line: lines[(lineNumber - 1)])) {
                    checkedCount++;

                    if (citation.Kind == CitationKind.File) {
                        if (HistoricalFileNames.Contains(value: citation.Target, comparer: StringComparer.Ordinal) || fileNameIndex.Contains(item: citation.Target)) {
                            continue;
                        }

                        var message = $"{document}:{lineNumber}: cited filename '{citation.Target}' exists nowhere under src/, docs/, tests/, build/, or .claude/skills/";

                        (enforceBareFileNames ? failures : advisories).Add(item: message);

                        continue;
                    }

                    if (HistoricalCitations.Contains(value: citation.Target, comparer: StringComparer.Ordinal)) {
                        continue;
                    }

                    if (!TryResolve(target: citation.Target, documentDirectory: documentDirectory, repositoryRoot: repositoryRoot)) {
                        failures.Add(item: $"{document}:{lineNumber}: {citation.KindName} '{citation.Target}' does not resolve");
                    }
                }
            }
        }

        Console.WriteLine(value: $"---- documents: {documents.Count()}; citations checked: {checkedCount}; failures: {failures.Count}; advisories: {advisories.Count} ----");

        foreach (var advisory in advisories) {
            Console.WriteLine(value: $"note: {advisory}");
        }

        if (failures.Count != 0) {
            foreach (var failure in failures) {
                Console.WriteLine(value: $"FAIL: {failure}");
            }

            return 1;
        }

        Console.WriteLine(value: "PASS: every relative link and cited repository path in the checked documents resolves.");

        return 0;
    }

    private enum CitationKind { Link, Path, File }
    private sealed record Citation(CitationKind Kind, string Target) {
        public string KindName => (Kind switch { CitationKind.Link => "link", CitationKind.Path => "path", _ => "file" });
    }

    private static IEnumerable<Citation> Citations(string line) {
        foreach (Match match in MarkdownLink.Matches(input: line)) {
            var target = match.Groups[1].Value;

            if (ExternalScheme.IsMatch(input: target) || target.StartsWith(value: '#')) {
                continue;
            }

            target = target.Split(separator: '#', count: 2)[0];

            if (target.Length != 0) {
                yield return new Citation(Kind: CitationKind.Link, Target: target);
            }
        }

        foreach (Match match in Backticked.Matches(input: line)) {
            var token = match.Groups[1].Value;

            if (RootedPath.IsMatch(input: token)) {
                yield return new Citation(Kind: CitationKind.Path, Target: token);
            } else if (BareFileName.IsMatch(input: token)) {
                yield return new Citation(Kind: CitationKind.File, Target: token);
            }
        }
    }
    private static bool TryResolve(string target, string documentDirectory, string repositoryRoot) =>
        (File.Exists(path: Path.Combine(path1: documentDirectory, path2: target))
            || Directory.Exists(path: Path.Combine(path1: documentDirectory, path2: target))
            || File.Exists(path: Path.Combine(path1: repositoryRoot, path2: target))
            || Directory.Exists(path: Path.Combine(path1: repositoryRoot, path2: target)));
    private static HashSet<string> BuildFileNameIndex(string repositoryRoot) {
        var index = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var tree in IndexedTrees) {
            var treePath = Path.Combine(path1: repositoryRoot, path2: tree.Replace(newChar: Path.DirectorySeparatorChar, oldChar: '/'));

            if (!Directory.Exists(path: treePath)) {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path: treePath, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
                index.Add(item: Path.GetFileName(path: file));
            }
        }

        foreach (var file in Directory.EnumerateFiles(path: repositoryRoot, searchOption: SearchOption.TopDirectoryOnly, searchPattern: "*")) {
            index.Add(item: Path.GetFileName(path: file));
        }

        return index;
    }
    private static int Usage() {
        Console.Error.WriteLine(
            value:
                """
                doc-links [<document> ...]

                  no arguments        check the world-documentation set this verb ships with
                  <document> ...      check exactly the named repository-relative markdown files instead

                Checks every relative markdown link and cited repository path in the checked documents resolves,
                plus every backticked bare filename against an index swept from src/, docs/, tests/, build/, and
                .claude/skills/ (enforced for a document under src/, advisory elsewhere). Runs one control first —
                a deliberately nonexistent path must fail resolution — so a green run proves the checker can fail.

                Exit codes: 0 every citation resolved, 1 one or more citations did not resolve, 2 usage/refusal.
                """);

        return 2;
    }
}
