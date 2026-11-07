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
    [GeneratedRegex(@"^(?<indent>\s*)(?<fence>`{3,}|~{3,})(?<info>.*)$")]
    private static partial Regex Fence();
    [GeneratedRegex(@"^(?<indent>\s*)(?:[-+*]|\d+[.)])\s+")]
    private static partial Regex Bullet();
    [GeneratedRegex(@"(?<![\w/#]):(?<start>\d+)(?:[-–](?<end>\d+))?(?!\d)")]
    private static partial Regex Continuation();
    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://[^\s`)>]+", RegexOptions.IgnoreCase)]
    private static partial Regex Address();

    private readonly Dictionary<string, string[]> m_trees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> m_revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string[]? Lines, string Why)> m_blobs = new(StringComparer.Ordinal);

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
        if (!m_trees.TryGetValue(revision, out var paths)) {
            var listing = CliGit.Run(repositoryRoot, "ls-tree", "-r", "--name-only", "-z", revision, "--");
            paths = listing.ExitCode == 0 ? listing.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
            m_trees[revision] = paths;
        }
        return paths;
    }

    private (string[]? Lines, string Why) Read(Occurrence occurrence, string document) {
        if (!m_revisions.TryGetValue(occurrence.Revision, out var commit)) {
            var resolved = CliGit.Run(repositoryRoot, "rev-parse", "--verify", "--end-of-options", occurrence.Revision + "^{commit}");
            commit = resolved.ExitCode == 0 ? resolved.Stdout.Trim() : null;
            m_revisions[occurrence.Revision] = commit;
        }
        if (commit is null) {
            return (null, $"revision not found: {occurrence.Revision}");
        }
        var paths = Tree(commit);
        var path = Uri.UnescapeDataString(occurrence.Path).Replace('\\', '/');
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) {
            path = new Uri(path).LocalPath.Replace('\\', '/');
        }
        var relative = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(Path.Combine(repositoryRoot, document))).Replace('\\', '/');
        var neighbor = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(Path.Combine(repositoryRoot, Path.GetDirectoryName(relative) ?? "", path))).Replace('\\', '/');
        // A report may link to its original worktree. Only a unique suffix in this revision identifies its blob.
        var candidates = paths.Where(candidate => candidate == neighbor || candidate == path).ToArray();
        if (candidates.Length == 0) {
            candidates = paths.Where(candidate => candidate.EndsWith('/' + path, StringComparison.Ordinal) || candidate.EndsWith('.' + path, StringComparison.Ordinal) || path.EndsWith('/' + candidate, StringComparison.Ordinal)).ToArray();
        }
        if (candidates.Length != 1) {
            return (null, candidates.Length == 0 ? $"file or revision not found at {occurrence.Revision}" : "path is ambiguous; cite a repository-relative path");
        }
        var key = $"{commit}:{candidates[0]}";
        if (!m_blobs.TryGetValue(key, out var blob)) {
            var read = CliGit.Run(repositoryRoot, "show", key);
            blob = read.ExitCode == 0 ? (ReadLines(read.Stdout), "") : (null, $"cannot read {key}: {read.Stderr.Trim()}");
            m_blobs[key] = blob;
        }
        return blob;
    }

    private static string[] ReadLines(string text) {
        var lines = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) {
            lines.Add(line);
        }
        return [.. lines];
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
                if (char.IsWhiteSpace(character)) {
                    pending = text.Length != 0;
                } else {
                    if (pending) {
                        text.Append(' ');
                        origins.Add(number);
                    }
                    text.Append(character);
                    origins.Add(number);
                    pending = false;
                }
            }
            pending = text.Length != 0;
        }
        return (text.ToString(), [.. origins]);
    }

    private static int Number(Group group, int fallback = 0) => !group.Success ? fallback : int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static List<Occurrence> Extract(string line, int number, string revision) {
        var occurrences = new List<Occurrence>();
        // A link's label and target describe one citation. Prefer the target's full path over a bare label.
        var text = Link().Replace(line, match => {
            var target = match.Groups["target"].Value;
            if (target.Contains("://", StringComparison.Ordinal) && !target.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) {
                return new string(' ', match.Length);
            }
            var targetLocation = Location().Match(target);
            var labelLocation = Location().Match(match.Groups["label"].Value);
            if (targetLocation.Success || labelLocation.Success) {
                var location = targetLocation.Success ? targetLocation : labelLocation;
                var path = !targetLocation.Success || target.Contains("://", StringComparison.Ordinal) ? target.Split('#')[0] : location.Groups["path"].Value;
                var start = Number(location.Groups["start"]);
                occurrences.Add(new(number, path, location.Groups["rev"].Success ? location.Groups["rev"].Value : revision, start, Number(location.Groups["end"], start)));
                return new string(' ', match.Length);
            }
            return match.Value;
        });
        text = Address().Replace(text, " ");
        foreach (Match location in Location().Matches(text)) {
            var start = Number(location.Groups["start"]);
            occurrences.Add(new(number, location.Groups["path"].Value, location.Groups["rev"].Success ? location.Groups["rev"].Value : revision, start, Number(location.Groups["end"], start)));
        }
        // Compact lists such as `file.cs:12`, `:24`, `:65` retain the preceding path.
        foreach (Match continuation in Continuation().Matches(text)) {
            var previous = Location().Matches(text[..continuation.Index]).LastOrDefault();
            if (previous is null) {
                continue;
            }
            var start = Number(continuation.Groups["start"]);
            occurrences.Add(new(number, previous.Groups["path"].Value, previous.Groups["rev"].Success ? previous.Groups["rev"].Value : revision, start, Number(continuation.Groups["end"], start)));
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
        string? fence = null;
        var quote = new List<string>();
        var owners = new List<Occurrence>();
        for (var index = 0; index < lines.Count; index++) {
            var line = lines[index];
            var marker = Fence().Match(line);
            if (fence is not null) {
                if (marker.Success && marker.Groups["fence"].Value[0] == fence[0] && marker.Groups["fence"].Length >= fence.Length && marker.Groups["info"].Value.Trim().Length == 0) {
                    foreach (var owner in owners) {
                        owner.Quotes.Add([.. quote]);
                    }
                    fence = null;
                    previous = [];
                } else {
                    quote.Add(line);
                }
                continue;
            }
            if (marker.Success) {
                fence = marker.Groups["fence"].Value;
                var indent = marker.Groups["indent"].Length;
                owners = previous.Count != 0 ? previous : bullets.LastOrDefault(bullet => bullet.Indent < indent).Citations ?? [];
                quote = [];
                continue;
            }
            if (Revision().Match(line) is { Success: true } declaration) {
                revision = declaration.Groups["rev"].Value;
            }
            var found = Extract(line, index + 1, revision);
            all.AddRange(found);
            if (Bullet().Match(line) is { Success: true } bullet) {
                var indent = bullet.Groups["indent"].Length;
                bullets.RemoveAll(entry => entry.Indent >= indent);
                bullets.Add((indent, found));
            } else if (!string.IsNullOrWhiteSpace(line)) {
                var indent = line.Length - line.TrimStart().Length;
                bullets.RemoveAll(entry => entry.Indent >= indent);
            }
            if (!string.IsNullOrWhiteSpace(line)) {
                previous = found;
            }
        }
        if (fence is not null) {
            foreach (var owner in owners) {
                owner.Quotes.Add([]); // An unclosed quotation cannot verify.
            }
        }
        return all.Select(occurrence => Verify(occurrence, document)).ToArray();
    }

    private Result Verify(Occurrence occurrence, string document) {
        var label = $"{occurrence.Path}:{occurrence.Start}" + (occurrence.End == occurrence.Start ? "" : $"-{occurrence.End}");
        Result Result(string status, string detail) => new(occurrence.Line, label, status, $"{occurrence.Revision}: {detail}");
        var (source, why) = Read(occurrence, document);
        if (source is null) {
            return Result("not found", why);
        }
        if (occurrence.Start < 1 || occurrence.End < occurrence.Start || occurrence.End > source.Length) {
            return Result("not found", $"range outside file length {source.Length}");
        }
        if (occurrence.Quotes.Count == 0) {
            return Result("verified", "file and line range exist; no quotation");
        }
        var normalized = Normalize(source);
        var drift = new List<int>();
        foreach (var quote in occurrence.Quotes) {
            var needle = Normalize(quote).Text;
            var matches = new List<int>();
            if (needle.Length != 0) {
                for (var offset = normalized.Text.IndexOf(needle, StringComparison.Ordinal); offset >= 0; offset = normalized.Text.IndexOf(needle, offset + 1, StringComparison.Ordinal)) {
                    matches.Add(normalized.Lines[offset]);
                }
            }
            if (matches.Count == 0) {
                return Result("not found", "quotation does not occur in file (whitespace normalized)");
            }
            if (!matches.Any(line => (long)line >= (long)occurrence.Start - Window && (long)line <= (long)occurrence.End + Window)) {
                drift.Add(matches.MinBy(line => Math.Abs((long)line - occurrence.Start)));
            }
        }
        return drift.Count != 0 ? Result("drifted", $"quotation at true line {string.Join(", ", drift)}") : Result("verified", $"file, range and {occurrence.Quotes.Count} quotation(s) match within {Window} lines");
    }
}
