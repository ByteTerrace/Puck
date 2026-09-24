using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

using Puck.Cli.Source;

namespace Puck.Cli.Docs;

/// <summary><c>puck docs links</c> — checks every relative markdown link and cited repository path in a fixed
/// documentation set resolves. For each document it extracts: (a) every relative markdown link target
/// (<c>[text](path)</c>, <c>[text](path#fragment)</c>, <c>[text](#fragment)</c>) — external schemes are skipped —
/// resolved against the document's own directory, then the repository root, and a fragment on a link into a
/// markdown file (the document itself when the link names no path) must equal one of that file's heading anchors,
/// slugged the way GitHub renders them (<see cref="HeadingAnchors"/>); (b) every backticked rooted repository path
/// (<c>src/...</c>, <c>docs/...</c>, <c>tests/...</c>, <c>build/...</c>) with a file extension, resolved the same
/// way; (c) every backticked bare filename with a source-ish extension, looked up in an index of every filename
/// under <c>src/</c>, <c>docs/</c>, <c>tests/</c>, <c>build/</c>, and <c>.claude/skills/</c> — enforced for
/// documents under <c>src/</c> (a project README cites its neighbors), advisory for documents under <c>docs/</c>,
/// which legitimately name out-of-repo files. Two controls — a deliberately nonexistent path must fail resolution,
/// and a deliberately absent fragment must miss a heading's anchors — run before any document, so a green run
/// proves the checker can turn red.</summary>
internal static class DocsLinksCommand {
    private static readonly string[] DefaultDocuments = [
        "src/Puck.World/README.md",
        "src/Puck.World.Client/README.md",
        "src/Puck.World/Audio/README.md",
        "src/Puck.World.Schema/README.md",
        "src/Puck.World.Server/README.md",
        "src/Puck.Attestation/README.md",
        "README.md",
        "docs/README.md",
        "docs/overview.md",
        "docs/getting-started.md",
        "docs/architecture/README.md",
        "docs/authoring/README.md",
        "docs/rendering/README.md",
        "docs/emulation/README.md",
        "docs/development/README.md",
        "docs/development/contributing.md",
        "docs/reference/README.md",
        "docs/plans/README.md",
        "docs/decisions/README.md",
        "docs/project-map.md",
        "docs/game/README.md",
    ];
    // CLAUDE.md rule 1 pins these paths as existing only in git history; docs/project-map.md states exactly
    // that where it names them, so their non-resolution is correct, not a broken citation.
    private static readonly string[] HistoricalCitations = ["src/Puck", "src/Puck.Avatars"];
    // A file a build writes into its own output rather than one the checkout holds: cited by name, never tracked.
    private static readonly string[] ProducedFileNames = ["dotnet.native.wasm"];
    private static readonly string[] IndexedTrees = ["src", "docs", "tests", "build", ".claude/skills"];
    private static readonly Regex MarkdownLink = new(
        options: RegexOptions.Compiled,
        pattern: @"\[[^\]]*\]\(([^)\s]+)\)"
    );
    private static readonly Regex Backticked = new(
        options: RegexOptions.Compiled,
        pattern: "`([^`]+)`"
    );
    private static readonly Regex RootedPath = new(
        options: RegexOptions.Compiled,
        pattern: @"^(src|docs|tests|build|\.claude)/[A-Za-z0-9._/\-]+\.[A-Za-z0-9]+$"
    );
    private static readonly Regex BareFileName = new(
        options: RegexOptions.Compiled,
        pattern: @"^[A-Za-z0-9._\-]+\.(cs|md|json|ps1|csproj|props|slnx|wasm|wat|py)$"
    );
    private static readonly Regex ExternalScheme = new(
        options: RegexOptions.Compiled | RegexOptions.IgnoreCase,
        pattern: @"^[a-z][a-z0-9+.\-]*:"
    );
    // CommonMark's ATX heading: up to three spaces of indent, one to six '#', then a space or the end of the line;
    // an optional closing run of '#' after a space is not part of the text.
    private static readonly Regex AtxHeading = new(
        options: RegexOptions.Compiled,
        pattern: @"^ {0,3}#{1,6}(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$"
    );
    // CommonMark's code fence: three or more backticks or tildes, indented at most three spaces.
    private static readonly Regex CodeFence = new(
        options: RegexOptions.Compiled,
        pattern: @"^ {0,3}(`{3,}|~{3,})"
    );
    private static readonly Regex InlineImage = new(
        options: RegexOptions.Compiled,
        pattern: @"!\[[^\]]*\]\([^)]*\)"
    );
    private static readonly Regex InlineLink = new(
        options: RegexOptions.Compiled,
        pattern: @"\[([^\]]*)\]\([^)]*\)"
    );
    private static readonly Regex HtmlTag = new(
        options: RegexOptions.Compiled,
        pattern: @"</?[A-Za-z][^>]*>"
    );
    // Emphasis delimiters render as no text; an underscore inside a word (snake_case) is literal and survives.
    private static readonly Regex EmphasisDelimiter = new(
        options: RegexOptions.Compiled,
        pattern: @"\*+|(?<![\p{L}\p{N}])_+|_+(?![\p{L}\p{N}])"
    );
    private static readonly Regex BackslashEscape = new(
        options: RegexOptions.Compiled,
        pattern: @"\\(\p{P}|\p{S})"
    );

    private static HashSet<string> BuildFileNameIndex(string repositoryRoot) {
        var index = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();

        foreach (var tree in IndexedTrees) {
            var treePath = Path.Combine(
                path1: repositoryRoot,
                path2: tree
            );

            if (Directory.Exists(path: treePath)) {
                roots.Add(item: treePath);
            }
        }

        foreach (var file in (FileWalk.Enumerate(
            verb: "docs links",
            roots: roots,
            include: [],
            exclude: []
        ) ?? [])) {
            index.Add(item: Path.GetFileName(path: file));
        }

        foreach (var file in Directory.EnumerateFiles(
            path: repositoryRoot,
            searchOption: SearchOption.TopDirectoryOnly,
            searchPattern: "*"
        )) {
            index.Add(item: Path.GetFileName(path: file));
        }

        return index;
    }
    private static IEnumerable<Citation> Citations(string line) {
        // A code span is literal text, never a link: `a[b](c)` inside backticks cites nothing.
        foreach (Match match in MarkdownLink.Matches(input: Backticked.Replace(
            input: line,
            replacement: string.Empty
        ))) {
            var target = match.Groups[1].Value;

            if (ExternalScheme.IsMatch(input: target)) {
                continue;
            }

            var parts = target.Split(
                separator: '#',
                count: 2
            );
            // An empty fragment (`path#`) names the top of the page, which every document has.
            var fragment = (((parts.Length == 2) && (parts[1].Length != 0))
                ? Uri.UnescapeDataString(stringToUnescape: parts[1])
                : null);

            if ((parts[0].Length != 0) || (fragment is not null)) {
                yield return new Citation(
                    Kind: CitationKind.Link,
                    Target: parts[0],
                    Fragment: fragment
                );
            }
        }

        foreach (Match match in Backticked.Matches(input: line)) {
            var token = match.Groups[1].Value;

            if (RootedPath.IsMatch(input: token)) {
                yield return new Citation(
                    Kind: CitationKind.Path,
                    Target: token
                );
            } else if (BareFileName.IsMatch(input: token)) {
                yield return new Citation(
                    Kind: CitationKind.File,
                    Target: token
                );
            }
        }
    }
    private static string? Resolve(string target, string documentDirectory, string repositoryRoot) =>
        (ResolveUnder(
            directory: documentDirectory,
            target: target
        ) ?? ResolveUnder(
            directory: repositoryRoot,
            target: target
        ));
    private static string? ResolveUnder(string directory, string target) {
        var candidate = Path.Combine(
            path1: directory,
            path2: target
        );

        return ((File.Exists(path: candidate) || Directory.Exists(path: candidate))
            ? candidate
            : null);
    }
    private static string Slug(string heading) {
        var rendered = new StringBuilder();
        var segments = heading.Split(separator: '`');

        // Even segments are markdown; odd segments are code-span content, which renders verbatim.
        for (var index = 0; (index < segments.Length); index++) {
            var segment = segments[index];

            if ((index % 2) == 0) {
                segment = InlineImage.Replace(
                    input: segment,
                    replacement: string.Empty
                );
                segment = InlineLink.Replace(
                    input: segment,
                    replacement: "$1"
                );
                segment = HtmlTag.Replace(
                    input: segment,
                    replacement: string.Empty
                );
                segment = EmphasisDelimiter.Replace(
                    input: segment,
                    replacement: string.Empty
                );
                segment = BackslashEscape.Replace(
                    input: segment,
                    replacement: "$1"
                );
                segment = WebUtility.HtmlDecode(value: segment);
            }

            rendered.Append(value: segment);
        }

        var slug = new StringBuilder(capacity: rendered.Length);

        foreach (var character in rendered.ToString().ToLowerInvariant()) {
            if (character == ' ') {
                slug.Append(value: '-');

                continue;
            }

            var category = char.GetUnicodeCategory(c: character);

            if (
                (character == '-') ||
                char.IsLetterOrDigit(c: character) ||
                (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or UnicodeCategory.ConnectorPunctuation)
            ) {
                slug.Append(value: character);
            }
        }

        return slug.ToString();
    }

    /// <summary>Returns every anchor GitHub renders for the headings of a markdown document: each ATX heading
    /// outside a code fence, reduced to its rendered text (code spans verbatim; link, image, HTML and emphasis markup
    /// dropped), lowercased, stripped of every character that is not a letter, mark, number, connector punctuation,
    /// space or hyphen, with each space turned into a hyphen. A repeated slug takes the suffix <c>-1</c>, <c>-2</c>,
    /// … in document order. This is the github-slugger rule GitHub renders these documents with; the API reference
    /// site (docs/api/docfx.json) renders none of them.</summary>
    /// <param name="lines">The document's lines.</param>
    /// <returns>The anchors, compared ordinally.</returns>
    internal static HashSet<string> HeadingAnchors(IEnumerable<string> lines) {
        var anchors = new HashSet<string>(comparer: StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        string? fence = null;

        foreach (var line in lines) {
            var fenceMatch = CodeFence.Match(input: line);

            if (fence is null) {
                if (fenceMatch.Success) {
                    fence = fenceMatch.Groups[1].Value;

                    continue;
                }
            } else {
                if (
                    fenceMatch.Success &&
                    (fenceMatch.Groups[1].Value[0] == fence[0]) &&
                    (fenceMatch.Groups[1].Value.Length >= fence.Length) &&
                    (line.Trim().Trim(trimChar: fence[0]).Length == 0)
                ) {
                    fence = null;
                }

                continue;
            }

            var heading = AtxHeading.Match(input: line);

            if (!heading.Success) {
                continue;
            }

            var original = Slug(heading: heading.Groups[1].Value.Trim());
            var slug = original;

            while (occurrences.ContainsKey(key: slug)) {
                occurrences[original]++;
                slug = $"{original}-{occurrences[original]}";
            }

            occurrences[slug] = 0;
            anchors.Add(item: slug);
        }

        return anchors;
    }
    internal static int Run(string repositoryRoot, string[] documents) {
        var fileNameIndex = BuildFileNameIndex(repositoryRoot: repositoryRoot);

        if (
            (Resolve(
                documentDirectory: repositoryRoot,
                repositoryRoot: repositoryRoot,
                target: "src/Puck.World/this-file-does-not-exist.md"
            ) is not null) ||
            fileNameIndex.Contains(item: "this-file-does-not-exist.md")
        ) {
            return CliExit.Refuse(
                verb: "docs links",
                what: "the control",
                why: "a deliberately nonexistent path resolved, so the checker cannot discriminate."
            );
        }

        var controlAnchors = HeadingAnchors(lines: ["# Control heading", "# Control heading"]);

        if (
            !controlAnchors.Contains(item: "control-heading-1") ||
            controlAnchors.Contains(item: "this-anchor-does-not-exist")
        ) {
            return CliExit.Refuse(
                verb: "docs links",
                what: "the anchor control",
                why: "a heading's anchors did not discriminate a present fragment from an absent one."
            );
        }

        var anchorCache = new Dictionary<string, HashSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var advisories = new List<string>();
        var checkedCount = 0;

        foreach (var document in documents) {
            var documentPath = Path.Combine(
                path1: repositoryRoot,
                path2: document
            );

            if (!File.Exists(path: documentPath)) {
                failures.Add(item: $"{document}: the document itself does not exist");

                continue;
            }

            var documentDirectory = (Path.GetDirectoryName(path: documentPath) ?? repositoryRoot);
            var enforceBareFileNames = document.Replace(
                newChar: '/',
                oldChar: '\\'
            ).StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "src/"
            );
            var lines = File.ReadAllLines(path: documentPath);

            for (var lineNumber = 1; (lineNumber <= lines.Length); lineNumber++) {
                foreach (var citation in Citations(line: lines[(lineNumber - 1)])) {
                    checkedCount++;

                    if (citation.Kind == CitationKind.File) {
                        if (
                            ProducedFileNames.Contains(
                            value: citation.Target,
                            comparer: StringComparer.Ordinal
                        ) ||
                            fileNameIndex.Contains(item: citation.Target)
                        ) {
                            continue;
                        }
                        // A leading-dot token names a partial file by its suffix (`.Drive.cs` beside `WorldReplayTape.cs`).
                        if (
                            citation.Target.StartsWith(value: '.') &&
                            fileNameIndex.Any(predicate: name => name.EndsWith(
                            value: citation.Target,
                            comparisonType: StringComparison.OrdinalIgnoreCase
                        ))
                        ) {
                            continue;
                        }

                        var message = $"{document}:{lineNumber}: cited filename '{citation.Target}' exists nowhere under src/, docs/, tests/, build/, or .claude/skills/";

                        (enforceBareFileNames
                            ? failures
                            : advisories).Add(item: message);

                        continue;
                    }

                    if (HistoricalCitations.Contains(
                        value: citation.Target,
                        comparer: StringComparer.Ordinal
                    )) {
                        continue;
                    }

                    var resolved = ((citation.Target.Length == 0)
                        ? documentPath
                        : Resolve(
                            documentDirectory: documentDirectory,
                            repositoryRoot: repositoryRoot,
                            target: citation.Target
                        ));

                    if (resolved is null) {
                        failures.Add(item: $"{document}:{lineNumber}: {citation.KindName} '{citation.Target}' does not resolve");

                        continue;
                    }

                    // A fragment is checked only where the target renders as markdown; a directory, a source file's
                    // line anchor, or any other resource carries no heading anchors to compare against.
                    if (
                        (citation.Fragment is null) ||
                        !File.Exists(path: resolved) ||
                        !resolved.EndsWith(
                            comparisonType: StringComparison.OrdinalIgnoreCase,
                            value: ".md"
                        )
                    ) {
                        continue;
                    }

                    if (!anchorCache.TryGetValue(
                        key: resolved,
                        value: out var anchors
                    )) {
                        anchors = HeadingAnchors(lines: File.ReadLines(path: resolved));
                        anchorCache[resolved] = anchors;
                    }

                    if (!anchors.Contains(item: citation.Fragment)) {
                        failures.Add(item: $"{document}:{lineNumber}: anchor '{citation.Target}#{citation.Fragment}' does not resolve");
                    }
                }
            }
        }

        Console.WriteLine(value: $"---- documents: {documents.Length}; citations checked: {checkedCount}; failures: {failures.Count}; advisories: {advisories.Count} ----");

        foreach (var advisory in advisories) {
            Console.WriteLine(value: $"note: {advisory}");
        }

        if (failures.Count != 0) {
            foreach (var failure in failures) {
                Console.WriteLine(value: $"FAIL: {failure}");
            }

            return CliExit.Failed;
        }

        Console.WriteLine(value: "PASS: every relative link, heading anchor and cited repository path in the checked documents resolves.");

        return CliExit.Success;
    }

    public static Command Create() {
        var documentsArgument = new Argument<string[]>(name: "document") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Repository-relative markdown files to check; absent, the manual's entry points and the World documentation.",
        };
        var command = new Command(
            description: "Check that every relative markdown link, heading anchor and cited repository path resolves.",
            name: "links"
        ) { documentsArgument };

        command.Detail(detail: """
            A link's #fragment into a markdown file (or a bare #fragment into the document itself)
            must name one of the target's heading anchors, slugged as GitHub renders them: the
            heading's text lowercased, punctuation dropped, spaces turned to hyphens, and a repeated
            heading suffixed -1, -2, ... in document order.

            Also checks every backticked bare filename against an index swept from src/, docs/,
            tests/, build/, and .claude/skills/: enforced for a document under src/, advisory
            elsewhere. Two controls run before any document (a deliberately nonexistent path must
            fail resolution, and a deliberately absent fragment must miss), so a green run proves
            the checker can turn red.

            Exit codes: 0 every citation resolved; 1 one or more did not; 2 a usage error or
            refusal.
            """);
        // The default is applied here rather than as a default value, which help would evaluate: reading it runs this
        // type's static initializer and compiles every citation pattern.
        command.SetAction(action: parseResult => (CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)
            ? Run(
                documents: ((parseResult.GetValue(argument: documentsArgument) is { Length: > 0 } documents)
                    ? documents
                    : DefaultDocuments),
                repositoryRoot: repositoryRoot
            )
            : CliExit.Refused));

        return command;
    }

    private enum CitationKind { Link, Path, File }
    private sealed record Citation(CitationKind Kind, string Target, string? Fragment = null) {
        public string KindName => (Kind switch { CitationKind.Link => "link", CitationKind.Path => "path", _ => "file" });
    }
}
