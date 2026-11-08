using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

using Puck.Cli.Source;

namespace Puck.Cli.Docs;

/// <summary><c>puck docs links</c> — the check that keeps the hand-written corpus (<see cref="DocsCorpus"/>) from
/// rotting. Beyond the citations below it resolves every cited C# name against the declaration index
/// (<see cref="DocsSymbols"/>) and fails on rot markers (<see cref="DocsRot"/>). For each document it extracts: (a)
/// every relative markdown link target
/// (<c>[text](path)</c>, <c>[text](path#fragment)</c>, <c>[text](#fragment)</c>) — external schemes are skipped —
/// resolved against the document's own directory, then the repository root, and a fragment on a link into a
/// markdown file (the document itself when the link names no path) must equal one of that file's heading anchors,
/// slugged the way GitHub renders them (<see cref="HeadingAnchors"/>); (b) every backticked rooted repository path
/// (<c>src/...</c>, <c>docs/...</c>, <c>tests/...</c>, <c>build/...</c>) with a file extension, resolved the same
/// way; (c) every backticked bare filename with a source-ish extension, looked up in an index of every filename
/// under <c>src/</c>, <c>docs/</c>, <c>tests/</c>, <c>build/</c>, <c>worlds/</c>, and <c>.claude/skills/</c> — enforced for
/// documents under <c>src/</c> and <c>tests/</c> (a project README cites its neighbors), advisory elsewhere, where a
/// page legitimately names out-of-repo files. Three controls — a deliberately nonexistent path must fail resolution,
/// a deliberately absent fragment must miss a heading's anchors, and this verb's own entry point must resolve while a
/// member it lacks does not — run before any document, so a green run proves the checker can turn red.</summary>
public static class DocsLinksCommand {
    // AGENTS.md rule 1 pins these paths as existing only in git history; docs/project-map.md states exactly
    // that where it names them, so their non-resolution is correct, not a broken citation.
    private static readonly string[] HistoricalCitations = ["src/Puck", "src/Puck.Avatars"];
    // A file a build writes into its own output rather than one the checkout holds: cited by name, never tracked.
    private static readonly string[] ProducedFileNames = ["dotnet.native.wasm", "summary.json", "install-intent.json", "folder.json"];
    private static readonly string[] IndexedTrees = ["src", "docs", "tests", "build", "worlds", ".claude/skills"];
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
        var fence = new MarkdownFence();

        foreach (var line in lines) {
            if (fence.InCode(line: line)) {
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

    public static int Run(string repositoryRoot, string[] documents) {
        var started = Stopwatch.GetTimestamp();
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

        var symbolControl = DocsSymbols.CitedName(span: $"{nameof(DocsLinksCommand)}.{nameof(Run)}()");

        if ((symbolControl is null) || (DocsSymbols.CitedName(span: "storage.userId") is not null)) {
            return CliExit.Refuse(
                verb: "docs links",
                what: "the symbol control",
                why: "the symbol shape did not tell a C# member from a document field path."
            );
        }

        var symbols = DocsSymbols.Build(repositoryRoot: repositoryRoot);

        if ((symbols.Resolve(name: symbolControl) != DocsSymbolVerdict.Resolved) || (symbols.Resolve(name: $"{nameof(DocsLinksCommand)}.ThisMemberDoesNotExist") != DocsSymbolVerdict.Missing)) {
            return CliExit.Refuse(
                verb: "docs links",
                what: "the symbol control",
                why: "this verb's own entry point did not resolve, or a member it lacks did, so the index cannot discriminate."
            );
        }

        var branches = DocsRot.Branches.TryRead(
            repositoryRoot: repositoryRoot,
            why: out var branchesWhy
        );
        var firstCommit = DocsRot.ReadFirstCommit(repositoryRoot: repositoryRoot);
        var rot = new DocsRot(
            branches: branches,
            firstCommit: firstCommit,
            literalDates: symbols.LiteralDates,
            pinnedHashes: DocsRot.HexRuns(path: Path.Combine(
                path1: repositoryRoot,
                path2: "THIRD-PARTY-NOTICES.md"
            ))
        );
        var anchorCache = new Dictionary<string, HashSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);
        var failures = new List<(string Category, string Message)>();
        var advisories = new List<string>();
        var checkedCount = 0;
        var codeCitations = new DocsCodeCitations(repositoryRoot: repositoryRoot);

        foreach (var document in documents) {
            var documentPath = Path.Combine(
                path1: repositoryRoot,
                path2: document
            );

            if (!File.Exists(path: documentPath)) {
                failures.Add(item: ("missing", $"{document}: the document itself does not exist"));

                continue;
            }

            var documentDirectory = (Path.GetDirectoryName(path: documentPath) ?? repositoryRoot);
            var relative = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(path: documentPath)).Replace(newChar: '/', oldChar: '\\');
            var enforceBareFileNames = (relative.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "src/"
            ) || relative.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "tests/"
            ));
            var durable = DocsCorpus.IsDurable(relativePath: relative);
            var corpus = DocsCorpus.IsDocument(relativePath: relative);
            var lines = File.ReadAllLines(path: documentPath);
            var fence = new MarkdownFence();

            var locations = codeCitations.Check(document: document, lines: lines);

            foreach (var location in locations) {
                checkedCount++;
                var message = $"{document}:{location.DocumentLine}: {location.Status}: {location.Citation} ({location.Detail})";

                Console.WriteLine(value: message);
                if (corpus && durable) {
                    advisories.Add(item: $"{document}:{location.DocumentLine}: '{location.Citation}' cites a line, which drifts; cite the symbol by name");
                }
                if (location.Status != "verified") {
                    failures.Add(item: ("code-citation", message));
                }
            }
            Console.WriteLine(value: $"citations: {document}: verified {locations.Count(predicate: location => (location.Status == "verified"))}; drifted {locations.Count(predicate: location => (location.Status == "drifted"))}; not found {locations.Count(predicate: location => (location.Status == "not found"))}");

            for (var lineNumber = 1; (lineNumber <= lines.Length); lineNumber++) {
                var line = lines[(lineNumber - 1)];

                var code = fence.InCode(line: line);

                foreach (var (category, message) in (corpus ? rot.Line(
                    code: code,
                    line: line
                ) : [])) {
                    failures.Add(item: (category, $"{document}:{lineNumber}: {message}"));
                }

                // A fenced block is an example or a transcript; neither its spans nor its links are citations.
                if (code) { continue; }
                foreach (Match span in Backticked.Matches(input: line)) {
                    var content = span.Groups[1].Value;

                    if (DocsSymbols.CitedName(span: content) is { } name) {
                        checkedCount++;

                        switch (symbols.Resolve(name: name)) {
                            case DocsSymbolVerdict.Missing:
                                failures.Add(item: ("symbol", $"{document}:{lineNumber}: symbol '{name}' names no declaration under src/ or tests/"));

                                break;
                            case DocsSymbolVerdict.Unknown:
                                advisories.Add(item: $"{document}:{lineNumber}: symbol '{name}' is neither Puck's nor the runtime's, so its currency is not checked");

                                break;
                            default:
                                break;
                        }
                    }

                    if (corpus && (rot.Branch(span: content) is { } branchMessage)) {
                        failures.Add(item: ("branch", $"{document}:{lineNumber}: {branchMessage}"));
                    }
                }

                foreach (var citation in Citations(line: line)) {
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

                        var message = $"{document}:{lineNumber}: cited filename '{citation.Target}' exists nowhere under src/, docs/, tests/, build/, worlds/, or .claude/skills/";

                        if (enforceBareFileNames) {
                            failures.Add(item: ("file", message));
                        } else {
                            advisories.Add(item: message);
                        }

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
                        failures.Add(item: (citation.KindName, $"{document}:{lineNumber}: {citation.KindName} '{citation.Target}' does not resolve"));

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
                        failures.Add(item: ("anchor", $"{document}:{lineNumber}: anchor '{citation.Target}#{citation.Fragment}' does not resolve"));
                    }
                }
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(startingTimestamp: started);
        var byCategory = failures.GroupBy(keySelector: static failure => failure.Category)
            .OrderBy(
                comparer: StringComparer.Ordinal,
                keySelector: static group => group.Key
            )
            .Select(selector: static group => $"{group.Key} {group.Count()}");

        Console.WriteLine(value: $"---- documents: {documents.Length}; declarations indexed: {symbols.DeclarationCount}; citations checked: {checkedCount}; failures: {failures.Count}{((failures.Count == 0) ? string.Empty : $" ({string.Join(separator: ", ", values: byCategory)})")}; advisories: {advisories.Count}; {elapsed.TotalSeconds.ToString(format: "0.0", provider: CultureInfo.InvariantCulture)}s ----");

        if (branches is null) {
            Console.WriteLine(value: $"note: branch names were not checked: {branchesWhy}.");
        }

        if (firstCommit is null) {
            Console.WriteLine(value: "note: the clone is shallow, so no date is exempt as history before the first commit.");
        }

        foreach (var advisory in advisories) {
            Console.WriteLine(value: $"note: {advisory}");
        }

        if (failures.Count != 0) {
            foreach (var (category, message) in failures) {
                Console.WriteLine(value: $"FAIL [{category}]: {message}");
            }

            return CliExit.Failed;
        }

        Console.WriteLine(value: "PASS: every link, anchor, cited path, symbol and code quotation resolves; no failing rot marker.");

        return CliExit.Success;
    }
    public static Command Create() {
        var documentsArgument = new Argument<string[]>(name: "document") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = "Repository-relative markdown files to check; absent, the whole hand-written corpus.",
        };
        var command = new Command(
            description: "Check markdown links, paths, symbols, code citations and quotations; check corpus rot markers.",
            name: "links"
        ) { documentsArgument };

        command.Detail(detail: """
            With no document named, checks the hand-written corpus, derived from the tree: AGENTS.md
            and README.md at the root, every markdown file under docs/ (not the image packs in
            docs/game/art/*/), under .claude/skills/ (not evals/) and under worlds/, and every
            README.md under src/ and tests/. A page whose opening lines say "Generated by ...; do
            not hand-edit" is left to its generator's check.

            A link's #fragment into a markdown file (or a bare #fragment into the document itself)
            must name one of the target's heading anchors, slugged as GitHub renders them: the
            heading's text lowercased, punctuation dropped, spaces turned to hyphens, and a repeated
            heading suffixed -1, -2, ... in document order.

            Also checks every backticked bare filename against an index swept from src/, docs/,
            tests/, build/, worlds/, and .claude/skills/: enforced for a document under src/ or
            tests/, advisory elsewhere.

            A code span outside a fenced block shaped like a dotted C# name (each segment opens
            with a capital: RuleCapacity.MaxWorkUnitsPerTick, DocsCommand.Create()) must name a type,
            member, namespace or project declared under src/ or tests/, read from the syntax index
            puck declarations uses, or a type of the .NET runtime.

            Rot markers fail the run: a date (an ISO date, or a month and a day) other than one
            the code spells in a string literal under src/ (a protocol revision); a commit SHA, a
            run of 7 to 40 hex characters on a line that speaks of commits, merges or tips; a path
            to a concrete entry under .claude/scratch/, a jobs/ directory or AppData/Local/Temp; a
            code span naming a branch (shaped like an existing one, or called a branch) that exists
            neither locally nor on a remote. These policy checks apply only to corpus pages.
            A line citation in a durable corpus page is advisory: cite a symbol instead.
            Nothing is recorded: every check reads the current tree and refs.
            Branch names are not checked when no remote-tracking branch exists.

            Every prose path:line, path:start-end and path#Lline citation is checked against HEAD,
            including in explicitly named reports outside the corpus. Citation revision: <revision>
            selects a document's source revision; path@revision:line overrides it for one citation.
            An introduced fenced quote must occur verbatim after whitespace runs are collapsed,
            starting within five lines of the cited range. A quote elsewhere reports drift and its
            true line; a missing file, invalid range or absent quote reports not found. Both fail.
            A citation immediately above a fence (blank lines allowed), or in its owning bullet,
            introduces the quote. Ellipses are literal. Each citation reports its verdict; locations
            without a quotation verify only file existence and bounds. Examples inside fences are
            not read as citations. Scratch reports may carry dates, revisions and scratch paths.

            Three controls run before any document (a deliberately nonexistent path must fail
            resolution, a deliberately absent fragment must miss, and this verb's own entry point
            must resolve while a member it lacks does not), so a green run proves the checker can
            turn red.

            Exit codes: 0 every check passed; 1 one or more failed; 2 a usage error or refusal.
            """);
        // The default is applied here rather than as a default value, which help would evaluate: reading it runs this
        // type's static initializer and compiles every citation pattern.
        command.SetAction(action: parseResult => {
            if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
                return CliExit.Refused;
            }

            if (parseResult.GetValue(argument: documentsArgument) is { Length: > 0 } documents) {
                // A named file that is not markdown holds no link this verb reads, so checking it would pass on nothing.
                if (documents.FirstOrDefault(predicate: static document => !string.Equals(a: Path.GetExtension(path: document), b: ".md", comparisonType: StringComparison.OrdinalIgnoreCase)) is { } other) {
                    return CliExit.Refuse(verb: "docs links", what: CliPaths.ToDisplay(fullPath: Path.GetFullPath(path: other)), why: "is not a markdown document.");
                }

                return Run(
                    documents: documents,
                    repositoryRoot: repositoryRoot
                );
            }

            return (DocsCorpus.TryEnumerate(
                documents: out var corpus,
                error: out var error,
                repositoryRoot: repositoryRoot
            )
                ? Run(
                    documents: [.. corpus],
                    repositoryRoot: repositoryRoot
                )
                : CliExit.Refuse(
                    verb: "docs links",
                    what: "the documentation corpus",
                    why: error
                ));
        });

        return command;
    }

    private enum CitationKind { Link, Path, File }
    private sealed record Citation(CitationKind Kind, string Target, string? Fragment = null) {
        public string KindName => (Kind switch { CitationKind.Link => "link", CitationKind.Path => "path", _ => "file" });
    }
}
