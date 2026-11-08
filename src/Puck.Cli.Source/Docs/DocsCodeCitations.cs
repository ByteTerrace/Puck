using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Puck.Cli.Docs;

/// <summary>Checks source locations and their fenced quotations against Git blobs, never another worktree's files.</summary>
public sealed partial class DocsCodeCitations(string repositoryRoot) {
    /// <summary>The permitted displacement of a quotation's first line from its cited range.</summary>
    public const int Window = 5;

    [GeneratedRegex(@"(?<![\w./-])(?<path>(?:\.?\.?/|/)?[\w.-]+(?:/[\w.-]+)*\.[A-Za-z][A-Za-z0-9]*)(?:@(?<rev>[\w./~^+-]+))?(?::|#L)(?<start>\d+)(?:[-–](?:L)?(?<end>\d+))?(?!\d)")]
    private static partial Regex Location();
    [GeneratedRegex(@"\[(?<label>[^\]]*)\]\((?<target>[^)]+)\)")]
    private static partial Regex Link();
    [GeneratedRegex(@"^\s*Citation revision:\s*`?(?<rev>[\w./~^+-]+)`?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Revision();
    [GeneratedRegex(@"^(?<indent>\s*)(?:[-+*]|\d+[.)])\s+")]
    private static partial Regex Bullet();
    [GeneratedRegex(@"(?<![\w/#]):(?<start>\d+)(?:[-–](?<end>\d+))?(?!\d)")]
    private static partial Regex Continuation();
    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://[^\s`)>]+", RegexOptions.IgnoreCase)]
    private static partial Regex Address();
    // A pasted listing's line-number gutter: `271: code` or the Read tool's `271\tcode`.
    [GeneratedRegex(@"^\s*\d+(?::\s?|\t)")]
    private static partial Regex Gutter();

    private readonly Dictionary<string, string[]> m_trees = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, string?> m_revisions = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, (string[]? Lines, string Why)> m_blobs = new(comparer: StringComparer.Ordinal);

    /// <summary>A checked occurrence, with the source line of the citation in the document.</summary>
    /// <param name="DocumentLine">One-based document line.</param>
    /// <param name="Citation">The cited path and range.</param>
    /// <param name="Status">verified, drifted, or not found.</param>
    /// <param name="Detail">The revision, true quotation line, or reason for failure.</param>
    public sealed record Result(int DocumentLine, string Citation, string Status, string Detail);

    private sealed class Occurrence(int line, string path, string revision, int start, int end) {
        public int Line { get; } = line;
        public string Path { get; } = path;
        public string Revision { get; } = revision;
        public int Start { get; } = start;
        public int End { get; } = end;
        public List<string[]> Quotes { get; } = [];
    }

    private string[] Tree(string revision) {
        if (!m_trees.TryGetValue(key: revision, value: out var paths)) {
            var listing = CliGit.Run(repositoryRoot, "ls-tree", "-r", "--name-only", "-z", revision, "--");

            paths = ((listing.ExitCode == 0) ? listing.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0') : []);
            m_trees[revision] = paths;
        }
        return paths;
    }
    private (string[]? Lines, string Why) Read(Occurrence occurrence, string document) {
        if (!m_revisions.TryGetValue(key: occurrence.Revision, value: out var commit)) {
            var resolved = CliGit.Run(repositoryRoot, "rev-parse", "--verify", "--end-of-options", (occurrence.Revision + "^{commit}"));

            commit = ((resolved.ExitCode == 0) ? resolved.Stdout.Trim() : null);
            m_revisions[occurrence.Revision] = commit;
        }
        if (commit is null) {
            return (null, $"revision not found: {occurrence.Revision}");
        }
        var paths = Tree(revision: commit);
        var path = Uri.UnescapeDataString(stringToUnescape: occurrence.Path).Replace(newChar: '/', oldChar: '\\');

        if (path.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "file://")) {
            path = new Uri(uriString: path).LocalPath.Replace(newChar: '/', oldChar: '\\');
        }
        var relative = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(path: Path.Combine(path1: repositoryRoot, path2: document))).Replace(newChar: '/', oldChar: '\\');
        var neighbor = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(path: Path.Combine(path1: repositoryRoot, path2: (Path.GetDirectoryName(path: relative) ?? ""), path3: path))).Replace(newChar: '/', oldChar: '\\');
        // A report may link to its original worktree. Only a unique suffix in this revision identifies its blob.
        var candidates = paths.Where(predicate: candidate => ((candidate == neighbor) || (candidate == path))).ToArray();

        if (candidates.Length == 0) {
            candidates = paths.Where(predicate: candidate => (candidate.EndsWith(comparisonType: StringComparison.Ordinal, value: ('/' + path)) || candidate.EndsWith(comparisonType: StringComparison.Ordinal, value: ('.' + path)) || path.EndsWith(comparisonType: StringComparison.Ordinal, value: ('/' + candidate)))).ToArray();
        }
        if (candidates.Length != 1) {
            return (null, ((candidates.Length == 0) ? $"file or revision not found at {occurrence.Revision}" : "path is ambiguous; cite a repository-relative path"));
        }
        var key = $"{commit}:{candidates[0]}";

        if (!m_blobs.TryGetValue(key: key, value: out var blob)) {
            var read = CliGit.Run(repositoryRoot, "show", key);

            blob = ((read.ExitCode == 0) ? (CliText.AllLines(text: read.Stdout), "") : (null, $"cannot read {key}: {read.Stderr.Trim()}"));
            m_blobs[key] = blob;
        }
        return blob;
    }
    // Collapse whitespace runs rather than deleting them: a quoted "a b" must not match "ab".
    private static (string Text, int[] Lines) Normalize(IEnumerable<string> lines) {
        var text = new StringBuilder();
        var origins = new List<int>();
        var pending = false;
        var number = 0;

        foreach (var line in lines) {
            number++;
            foreach (var character in line) {
                if (char.IsWhiteSpace(c: character)) {
                    pending = (text.Length != 0);
                } else {
                    if (pending) {
                        text.Append(value: ' ');
                        origins.Add(item: number);
                    }
                    text.Append(value: character);
                    origins.Add(item: number);
                    pending = false;
                }
            }
            pending = (text.Length != 0);
        }
        return (text.ToString(), [.. origins]);
    }
    private static int Number(Group group, int fallback = 0) => (!group.Success ? fallback : (int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0));
    private static List<Occurrence> Extract(string line, int number, string revision) {
        var occurrences = new List<Occurrence>();
        // A link's label and target describe one citation. Prefer the target's full path over a bare label.
        var text = Link().Replace(line, match => {
            var target = match.Groups["target"].Value;

            if (target.Contains(comparisonType: StringComparison.Ordinal, value: "://") && !target.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "file://")) {
                return new string(c: ' ', count: match.Length);
            }
            var targetLocation = Location().Match(input: target);
            var labelLocation = Location().Match(input: match.Groups["label"].Value);

            if (targetLocation.Success || labelLocation.Success) {
                var location = (targetLocation.Success ? targetLocation : labelLocation);
                var path = ((!targetLocation.Success || target.Contains(comparisonType: StringComparison.Ordinal, value: "://")) ? target.Split('#')[0] : location.Groups["path"].Value);
                var start = Number(location.Groups["start"]);

                occurrences.Add(item: new(number, path, (location.Groups["rev"].Success ? location.Groups["rev"].Value : revision), start, Number(location.Groups["end"], start)));
                return new string(c: ' ', count: match.Length);
            }
            return match.Value;
        });

        text = Address().Replace(input: text, replacement: " ");
        foreach (Match location in Location().Matches(input: text)) {
            var start = Number(location.Groups["start"]);

            occurrences.Add(item: new(number, location.Groups["path"].Value, (location.Groups["rev"].Success ? location.Groups["rev"].Value : revision), start, Number(location.Groups["end"], start)));
        }
        // Compact lists such as `file.cs:12`, `:24`, `:65` retain the preceding path.
        foreach (Match continuation in Continuation().Matches(input: text)) {
            var previous = Location().Matches(input: text[..continuation.Index]).LastOrDefault();

            if (previous is null) {
                continue;
            }
            var start = Number(continuation.Groups["start"]);

            occurrences.Add(item: new(number, previous.Groups["path"].Value, (previous.Groups["rev"].Success ? previous.Groups["rev"].Value : revision), start, Number(continuation.Groups["end"], start)));
        }
        return occurrences;
    }

    /// <summary>Checks every prose location and the fences introduced immediately above or by their owning bullet.
    /// A document may declare <c>Citation revision: &lt;revision&gt;</c>; an individual path may use <c>path@revision:line</c>.
    /// Unqualified locations use HEAD. Fenced examples themselves are not parsed as citations.</summary>
    /// <param name="document">The document path, relative to the repository or absolute.</param>
    /// <param name="lines">The document's lines.</param>
    /// <returns>One result per location occurrence; any missing quote dominates drift, which dominates verification.</returns>
    public IReadOnlyList<Result> Check(string document, IReadOnlyList<string> lines) {
        var revision = "HEAD";
        var all = new List<Occurrence>();
        var previous = new List<Occurrence>();
        var bullets = new List<(int Indent, List<Occurrence> Citations)>();
        var fence = new MarkdownFence();
        var quote = new List<string>();
        var owners = new List<Occurrence>();

        for (var index = 0; (index < lines.Count); index++) {
            var line = lines[index];
            var wasOpen = fence.IsOpen;
            var code = fence.InCode(line: line);

            if (wasOpen) {
                if (!fence.IsOpen) {
                    foreach (var owner in Owning(document: document, owners: owners, quote: [.. quote])) {
                        owner.Quotes.Add(item: [.. quote]);
                    }
                    previous = [];
                } else {
                    quote.Add(item: line);
                }
                continue;
            }
            if (code) {
                var indent = fence.Indent;

                owners = ((previous.Count != 0) ? previous : (bullets.LastOrDefault(predicate: bullet => (bullet.Indent < indent)).Citations ?? []));
                quote = [];
                continue;
            }
            if (Revision().Match(input: line) is { Success: true } declaration) {
                revision = declaration.Groups["rev"].Value;
            }
            var found = Extract(line: line, number: (index + 1), revision: revision);

            all.AddRange(collection: found);
            if (Bullet().Match(input: line) is { Success: true } bullet) {
                var indent = bullet.Groups["indent"].Length;

                bullets.RemoveAll(match: entry => (entry.Indent >= indent));
                bullets.Add(item: (indent, found));
            } else if (!string.IsNullOrWhiteSpace(value: line)) {
                var indent = (line.Length - line.TrimStart().Length);

                bullets.RemoveAll(match: entry => (entry.Indent >= indent));
            }
            if (!string.IsNullOrWhiteSpace(value: line)) {
                previous = found;
            }
        }
        if (fence.IsOpen) {
            foreach (var owner in owners) {
                owner.Quotes.Add(item: []); // An unclosed quotation cannot verify.
            }
        }
        return all.Select(selector: occurrence => Verify(document: document, occurrence: occurrence)).ToArray();
    }

    // A quotation as pasted from a numbered listing is still a quotation: when every non-blank line carries a line-number
    // gutter, the gutter is dropped, and a line that is only an elision (`...` or `…`) splits the quotation into pieces that
    // must each occur.
    private static IEnumerable<string[]> Pieces(string[] quote) {
        var numbered = (quote.Any(predicate: line => ((line.Trim().Length != 0) && !IsElision(line: line))) &&
            quote.Where(predicate: line => ((line.Trim().Length != 0) && !IsElision(line: line))).All(predicate: line => Gutter().IsMatch(input: line)));
        var piece = new List<string>();
        var any = false;

        foreach (var line in quote) {
            if (IsElision(line: line)) {
                if (piece.Count != 0) {
                    any = true;
                    yield return [.. piece];
                    piece.Clear();
                }
                continue;
            }
            piece.Add(item: (numbered ? Gutter().Replace(count: 1, input: line, replacement: "") : line));
        }
        if ((piece.Count != 0) || !any) {
            yield return [.. piece]; // Nothing but elisions is an empty quotation, which cannot verify.
        }
    }
    private static bool IsElision(string line) => (line.Trim() is "..." or "…");
    // A fence introduced by several locations (`a.cs:10-12` and `b.cs:40-44`, then one block each) belongs to the location
    // whose range it quotes; only a quotation no introducing location holds is charged to them all.
    private List<Occurrence> Owning(List<Occurrence> owners, string[] quote, string document) {
        if (owners.Count < 2) {
            return owners;
        }
        var fitting = owners.Where(predicate: owner => Pieces(quote: quote).All(predicate: piece => Near(document: document, owner: owner, piece: piece))).ToList();

        return ((fitting.Count != 0) ? fitting : owners);
    }
    private bool Near(Occurrence owner, string[] piece, string document) {
        var (source, _) = Read(document: document, occurrence: owner);
        var needle = Normalize(lines: piece).Text;

        if ((source is null) || (needle.Length == 0)) {
            return false;
        }
        var normalized = Normalize(lines: source);

        for (var offset = normalized.Text.IndexOf(comparisonType: StringComparison.Ordinal, value: needle); (offset >= 0); offset = normalized.Text.IndexOf(comparisonType: StringComparison.Ordinal, startIndex: (offset + 1), value: needle)) {
            var line = ((long)normalized.Lines[offset]);

            if ((line >= (((long)owner.Start) - Window)) && (line <= (((long)owner.End) + Window))) {
                return true;
            }
        }
        return false;
    }
    private Result Verify(Occurrence occurrence, string document) {
        var label = ($"{occurrence.Path}:{occurrence.Start}" + ((occurrence.End == occurrence.Start) ? "" : $"-{occurrence.End}"));

        Result Result(string status, string detail) => new(occurrence.Line, label, status, $"{occurrence.Revision}: {detail}");
        var (source, why) = Read(document: document, occurrence: occurrence);
        if (source is null) {
            return Result(detail: why, status: "not found");
        }
        if ((occurrence.Start < 1) || (occurrence.End < occurrence.Start) || (occurrence.End > source.Length)) {
            return Result("not found", $"range outside file length {source.Length}");
        }
        if (occurrence.Quotes.Count == 0) {
            return Result(detail: "file and line range exist; no quotation", status: "verified");
        }
        var normalized = Normalize(lines: source);
        var drift = new List<int>();

        foreach (var quote in occurrence.Quotes.SelectMany(selector: Pieces)) {
            var needle = Normalize(lines: quote).Text;
            var matches = new List<int>();

            if (needle.Length != 0) {
                for (var offset = normalized.Text.IndexOf(comparisonType: StringComparison.Ordinal, value: needle); (offset >= 0); offset = normalized.Text.IndexOf(comparisonType: StringComparison.Ordinal, startIndex: (offset + 1), value: needle)) {
                    matches.Add(item: normalized.Lines[offset]);
                }
            }
            if (matches.Count == 0) {
                return Result(detail: "quotation does not occur in file (whitespace normalized)", status: "not found");
            }
            if (!matches.Any(predicate: line => ((((long)line) >= (((long)occurrence.Start) - Window)) && (((long)line) <= (((long)occurrence.End) + Window))))) {
                drift.Add(item: matches.MinBy(keySelector: line => Math.Abs(value: (((long)line) - occurrence.Start))));
            }
        }
        return ((drift.Count != 0) ? Result("drifted", $"quotation at true line {string.Join(separator: ", ", values: drift)}") : Result("verified", $"file, range and {occurrence.Quotes.Count} quotation(s) match within {Window} lines"));
    }
}
