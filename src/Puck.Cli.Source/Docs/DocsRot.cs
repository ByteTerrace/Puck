using System.Globalization;
using System.Text.RegularExpressions;

namespace Puck.Cli.Docs;

/// <summary>
/// The markers of a page that has started to rot, each read from current state with nothing recorded: a date or a
/// commit SHA (a record of when the repository changed, which belongs in the commit), a path into an agent's scratch
/// or job output (which outlives nothing), and a branch that no longer exists. The rule each marker enforces is the documentation guide's
/// (docs/development/documentation.md, "Keep documents from rotting"); its exemptions are structural, each derived
/// from the tree: a date the code spells in a string literal (a protocol revision), a date before the repository's
/// first commit (a historical fact, which cannot record a change to it), and a SHA the third-party notices pin.
/// </summary>
internal sealed partial class DocsRot {
    private const string MonthNames = "January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec";
    private const string DayOfMonth = "(?:[12]\\d|3[01]|0?[1-9])";

    // An address names an external resource whose spelling is its own; a date or hash inside one is not this page's.
    [GeneratedRegex(pattern: @"<[a-z][a-z0-9+.\-]*://[^>\s]*>|\]\([^)\s]*\)|\b[a-z][a-z0-9+.\-]*://[^\s)>`]+", options: RegexOptions.IgnoreCase)]
    private static partial Regex Address();
    [GeneratedRegex(pattern: @"\b(?:19|20)\d\d-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])\b")]
    private static partial Regex IsoDate();
    // A month and a day in either order, with the year that may follow (group 1).
    [GeneratedRegex(pattern: $@"\b(?:(?:{MonthNames})\.? {DayOfMonth}(?:st|nd|rd|th)?|{DayOfMonth} (?:{MonthNames}))\b(?:,? ((?:19|20)\d\d)\b)?")]
    private static partial Regex MonthDay();
    // A run of lowercase hex holding at least one digit and one letter, standing alone (not inside a longer word, a
    // hex literal or a hyphenated name).
    [GeneratedRegex(pattern: @"(?<![\w.\-/#])(?=[0-9a-f]*[a-f])(?=[0-9a-f]*\d)[0-9a-f]{7,40}(?![\w\-])")]
    private static partial Regex HexRun();
    // The words that make a hex run a commit: it is named as one, or as the state a branch was at.
    [GeneratedRegex(pattern: @"\b(?:commit|commits|committed|sha|shas|merge|merged|tip|cherry-pick|cherry-picked|revert|reverted|rebase|rebased|landed|revision|HEAD)\b", options: RegexOptions.IgnoreCase)]
    private static partial Regex CommitContext();
    // A path into scratch: the lane scratch directory, an agent's job output directory, or the user's temporary
    // directory. Group 1 is what follows the directory, empty when the path names only the directory.
    [GeneratedRegex(pattern: @"(?:\.claude[/\\]scratch|\.(?:claude|codex)[/\\]jobs|(?i:AppData[/\\]Local[/\\]Temp))[/\\]([^\s`'"")\]]*)")]
    private static partial Regex ScratchPath();
    [GeneratedRegex(pattern: @"^[A-Za-z0-9][A-Za-z0-9._\-]*(?:/[A-Za-z0-9._\-]+)*$")]
    private static partial Regex BranchShape();

    private readonly IReadOnlySet<string> m_literalDates;
    private readonly IReadOnlySet<string> m_pinnedHashes;
    private readonly DateOnly? m_firstCommit;
    private readonly Branches? m_branches;

    /// <summary>Initializes a new instance of the <see cref="DocsRot"/> class.</summary>
    /// <param name="literalDates">The dates the code spells inside string literals: revision names it speaks, exempt.</param>
    /// <param name="pinnedHashes">The hex runs the third-party notices carry: upstream provenance pins, exempt.</param>
    /// <param name="firstCommit">The date of the repository's first commit, before which a date is history, or
    /// <see langword="null"/> when the clone is shallow and cannot say.</param>
    /// <param name="branches">The branches that exist, or <see langword="null"/> when there are none to judge against.</param>
    public DocsRot(IReadOnlySet<string> literalDates, IReadOnlySet<string> pinnedHashes, DateOnly? firstCommit, Branches? branches) {
        m_literalDates = literalDates;
        m_pinnedHashes = pinnedHashes;
        m_firstCommit = firstCommit;
        m_branches = branches;
    }

    /// <summary>Reads the date of the repository's first commit, or <see langword="null"/> when the clone is shallow
    /// (its oldest commit is not the first) or git cannot say.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The date, or <see langword="null"/>.</returns>
    public static DateOnly? ReadFirstCommit(string repositoryRoot) {
        var shallow = CliGit.Run(repositoryRoot, "rev-parse", "--is-shallow-repository");

        if ((shallow.ExitCode != 0) || (shallow.Stdout.Trim() != "false")) {
            return null;
        }

        var roots = CliGit.Run(repositoryRoot, "log", "--max-parents=0", "--format=%cs", "HEAD");

        return ((roots.ExitCode == 0)
            ? roots.Stdout.Split(
                options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
                separator: '\n'
            ).Select(selector: static text => (DateOnly.TryParseExact(
                format: "yyyy-MM-dd",
                provider: CultureInfo.InvariantCulture,
                result: out var date,
                s: text,
                style: DateTimeStyles.None
            ) ? date : (DateOnly?)null)).Min()
            : null);
    }

    private bool IsHistory(DateOnly last) => ((m_firstCommit is { } first) && (last < first));

    /// <summary>Returns the rot one line of a page shows, apart from the branch names its code spans cite. A date or
    /// SHA is rot anywhere, a transcript in a fenced block included; a scratch path inside a
    /// fenced block is an example command or tool output showing the shape, so only prose is checked for them.</summary>
    /// <param name="line">The line.</param>
    /// <param name="code">Whether the line belongs to a fenced block.</param>
    /// <returns>The findings, each a category and its message.</returns>
    public IEnumerable<(string Category, string Message)> Line(string line, bool code) {
        var text = Address().Replace(
            input: line,
            replacement: " "
        );

        foreach (Match match in IsoDate().Matches(input: text)) {
            if (
                !m_literalDates.Contains(item: match.Value) &&
                !(DateOnly.TryParseExact(
                    format: "yyyy-MM-dd",
                    provider: CultureInfo.InvariantCulture,
                    result: out var date,
                    s: match.Value,
                    style: DateTimeStyles.None
                ) && IsHistory(last: date))
            ) {
                yield return ("date", $"date '{match.Value}' records when the repository changed; the commit carries it");
            }
        }

        foreach (Match match in MonthDay().Matches(input: text)) {
            if (!(match.Groups[1].Success && IsHistory(last: new DateOnly(
                day: 31,
                month: 12,
                year: int.Parse(
                    provider: CultureInfo.InvariantCulture,
                    s: match.Groups[1].Value
                )
            )))) {
                yield return ("date", $"date '{match.Value}' records when the repository changed; the commit carries it");
            }
        }

        if (CommitContext().IsMatch(input: text)) {
            foreach (Match match in HexRun().Matches(input: text)) {
                if (!m_pinnedHashes.Contains(item: match.Value)) {
                    yield return ("sha", $"commit SHA '{match.Value}' pins a moment of the repository; the commit message carries it");
                }
            }
        }

        if (code) {
            yield break;
        }

        foreach (Match match in ScratchPath().Matches(input: line)) {
            var entry = match.Groups[1].Value;

            // The directory alone, or a placeholder entry (`<lane>-*`), states the convention; a concrete entry is one
            // run's output, gone when its worktree is.
            if ((entry.Length != 0) && (entry.IndexOfAny(anyOf: ['<', '{']) < 0) && (entry != "*")) {
                yield return ("scratch", $"scratch path '{match.Value}' names one run's output, which no reader can open");
            }
        }

    }
    /// <summary>Returns the finding for a code span that names a branch which no longer exists, or
    /// <see langword="null"/>. A span is a branch name when it is shaped like this repository's branches
    /// (<see cref="Branches.IsShaped"/>); prose that calls a word a branch (an <c>if</c> branch) is not enough.</summary>
    /// <param name="span">The code span's content.</param>
    /// <returns>The finding's message, or <see langword="null"/>.</returns>
    public string? Branch(string span) =>
        (((m_branches is not null) && BranchShape().IsMatch(input: span) && m_branches.IsShaped(name: span) && !m_branches.Exists(name: span))
            ? $"branch '{span}' exists neither locally nor on a remote"
            : null);
    /// <summary>Reads the hex runs a file carries, for the provenance pins of the third-party notices.</summary>
    /// <param name="path">The file, which need not exist.</param>
    /// <returns>The hex runs; none when the file is absent.</returns>
    public static HashSet<string> HexRuns(string path) =>
        (File.Exists(path: path)
            ? HexRun().Matches(input: File.ReadAllText(path: path)).Select(selector: static match => match.Value).ToHashSet(comparer: StringComparer.Ordinal)
            : new HashSet<string>(comparer: StringComparer.Ordinal));

    /// <summary>The branches that exist, read once from the local and remote-tracking refs (what <c>git branch -a</c>
    /// lists), and the shapes their names take.</summary>
    internal sealed class Branches {
        private readonly HashSet<string> m_names = new(comparer: StringComparer.Ordinal);
        private readonly HashSet<string> m_remotes = new(comparer: StringComparer.Ordinal);
        private readonly HashSet<string> m_namespaces = new(comparer: StringComparer.Ordinal);
        private readonly HashSet<string> m_families = new(comparer: StringComparer.Ordinal);

        private Branches() { }

        // A name that is a family member's: `worktree-agent-ae517e1c` belongs to `worktree-agent`.
        private static string? Family(string name) {
            var dash = name.LastIndexOf(value: '-');

            return ((dash > 0)
                ? name[..dash]
                : null);
        }
        private void Add(string name) {
            _ = m_names.Add(item: name);

            var slash = name.IndexOf(value: '/');

            if (slash > 0) {
                _ = m_namespaces.Add(item: name[..slash]);
            } else if (Family(name: name) is { } family) {
                _ = m_families.Add(item: family);
            }
        }

        /// <summary>Gets the number of distinct branch names read.</summary>
        public int Count => m_names.Count;

        /// <summary>Reads the refs, or returns <see langword="null"/> when git cannot list them or no remote-tracking
        /// branch exists: a shallow single-branch clone cannot tell a deleted branch from one it never fetched.</summary>
        /// <param name="repositoryRoot">The repository root.</param>
        /// <param name="why">Why the branches cannot be judged, or empty.</param>
        /// <returns>The branches, or <see langword="null"/>.</returns>
        public static Branches? TryRead(string repositoryRoot, out string why) {
            var listing = CliGit.Run(repositoryRoot, "for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes");

            if (listing.ExitCode != 0) {
                why = $"git for-each-ref failed: {listing.Stderr.Trim()}";

                return null;
            }

            var branches = new Branches();
            var remoteTracking = 0;

            foreach (var reference in listing.Stdout.Split(
                options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
                separator: '\n'
            )) {
                if (reference.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "refs/heads/"
                )) {
                    branches.Add(name: reference["refs/heads/".Length..]);
                } else if (reference.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "refs/remotes/"
                )) {
                    var full = reference["refs/remotes/".Length..];
                    var slash = full.IndexOf(value: '/');

                    if ((slash < 0) || (full[(slash + 1)..] == "HEAD")) {
                        continue;
                    }

                    _ = branches.m_names.Add(item: full);
                    _ = branches.m_remotes.Add(item: full[..slash]);
                    branches.Add(name: full[(slash + 1)..]);
                    remoteTracking++;
                }
            }

            if (remoteTracking == 0) {
                why = "no remote-tracking branch exists, so a branch missing here may only be unfetched; fetch the remote's branches to check branch names";

                return null;
            }

            why = string.Empty;

            return branches;
        }
        /// <summary>Returns whether a branch of this name exists locally or on a remote, spelled bare or with its
        /// remote.</summary>
        /// <param name="name">The name.</param>
        /// <returns><see langword="true"/> when it exists.</returns>
        public bool Exists(string name) => m_names.Contains(item: name);
        /// <summary>Returns whether a name is shaped like this repository's branches: it opens with a remote or a
        /// namespace an existing branch uses (<c>lane/</c>), or extends a family of existing names
        /// (<c>worktree-agent-…</c>).</summary>
        /// <param name="name">The name.</param>
        /// <returns><see langword="true"/> when the name is branch-shaped.</returns>
        public bool IsShaped(string name) {
            var slash = name.IndexOf(value: '/');

            if (slash > 0) {
                var head = name[..slash];

                return (m_remotes.Contains(item: head) || m_namespaces.Contains(item: head));
            }

            return ((Family(name: name) is { } family) && m_families.Contains(item: family));
        }
    }
}
