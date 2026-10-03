using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions;
using Puck.Hosting;

namespace Puck.Cli.WorktreeReport;

/// <summary>Reports local branches and worktrees whose work has landed, without changing the repository.</summary>
public static class WorktreeReportCommand {
    private const string Verb = "worktree-report";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly string[] DiffOptions = ["--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--binary", "--full-index", "--no-relative", "--src-prefix=a/", "--dst-prefix=b/", "--diff-algorithm=myers", "--no-indent-heuristic"];

    private sealed record Upstream(string Name, string Track);
    private sealed record Branch(string Name, string Head, Upstream? Upstream);
    private sealed record Worktree(string Path, string Head, string? Branch, string? Locked, string? Prunable);
    private sealed record Dirty(int Modified, int Untracked);
    private sealed record History(string Landed, int? Unlanded, DateTimeOffset? LastCommit, string? Unreadable);
    private sealed class Entry {
        [JsonPropertyOrder(7)] public long? AgeDays { get; init; }
        [JsonPropertyOrder(13)] public required List<string> Blockers { get; init; }
        [JsonPropertyOrder(0)] public string? Branch { get; init; }
        [JsonPropertyOrder(5)] public Dirty? Dirty { get; init; }
        [JsonPropertyOrder(2)] public required string Head { get; init; }
        [JsonPropertyOrder(3)] public required string Landed { get; init; }
        [JsonPropertyOrder(6)] public DateTimeOffset? LastCommit { get; init; }
        [JsonPropertyOrder(9)] public string? Locked { get; init; }
        [JsonPropertyOrder(10)] public string? Prunable { get; init; }
        [JsonPropertyOrder(12)] public bool Removable => (Blockers.Count == 0);
        [JsonPropertyOrder(4)] public int? Unlanded { get; init; }
        [JsonPropertyOrder(11)] public string? Unreadable { get; init; }
        [JsonPropertyOrder(8)] public Upstream? Upstream { get; init; }
        [JsonPropertyOrder(1)] public string? Worktree { get; init; }
    }

    // Optional locks refresh indexes; lazy fetch fills missing objects. Neither belongs in a report. Disable
    // fsmonitor too: even a status query can otherwise start a daemon or invoke a user-supplied hook.
    private static ChildProcessResult Git(string repository, string[] arguments, string? input = null) => CliGit.RunAsync(
        repository: repository,
        arguments: ["--no-optional-locks", "--no-lazy-fetch", "-c", "core.fsmonitor=false", "-c", "maintenance.auto=false", "-c", "gc.auto=0", .. arguments],
        input: input
    ).GetAwaiter().GetResult();
    private static string Failure(string operation, ChildProcessResult result) =>
        $"git {operation} exited {result.ExitCode}: {result.Stderr.Trim().Replace(newChar: '/', oldChar: '\\')}";
    private static string Read(string repository, string[] arguments, string? input = null, List<string>? warnings = null) {
        var result = Git(arguments: arguments, input: input, repository: repository);

        if (result.ExitCode != 0) {
            throw new InvalidOperationException(message: Failure(operation: arguments[0], result: result));
        }
        if (!string.IsNullOrWhiteSpace(value: result.Stderr)) {
            var reason = Failure(operation: arguments[0], result: result);

            if (warnings is null) { throw new InvalidOperationException(message: reason); }
            warnings.Add(item: reason);
        }
        return result.Stdout;
    }
    private static Branch[] ReadBranches(string repository, List<string> warnings) => Read(
        repository: repository,
        warnings: warnings,
        arguments: ["for-each-ref", "--format=%(refname:strip=2)%00%(objectname)%00%(upstream:short)%00%(upstream:track)", "refs/heads/"]
    ).Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n').Select(selector: static line => {
        var fields = line.TrimEnd(trimChar: '\r').Split(separator: '\0');

        return new Branch(Name: fields[0], Head: fields[1], Upstream: ((fields[2].Length == 0) ? null : new Upstream(Name: fields[2], Track: fields[3])));
    }).ToArray();
    private static string? LocalCounterpart(string repository, string reference, List<string> warnings) {
        var fetches = Git(repository: repository, arguments: ["config", "--null", "--get-regexp", @"^remote\..*\.fetch$"]);

        if ((fetches.ExitCode is not (0 or 1)) || !string.IsNullOrWhiteSpace(value: fetches.Stderr)) {
            throw new InvalidOperationException(message: Failure(operation: "config", result: fetches));
        }
        var counterparts = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var record in fetches.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0')) {
            var mapping = record[(record.IndexOf(value: '\n') + 1)..].TrimStart(trimChar: '+').Split(separator: ':');

            if ((mapping.Length != 2) || !mapping[0].StartsWith(comparisonType: StringComparison.Ordinal, value: "refs/heads/")) { continue; }
            var source = mapping[0];
            var destination = mapping[1];
            var wildcard = destination.IndexOf(value: '*');

            if (wildcard < 0) {
                if (reference != destination) { continue; }
            } else {
                var prefix = destination[..wildcard];
                var suffix = destination[(wildcard + 1)..];

                if ((reference.Length < (prefix.Length + suffix.Length)) ||
                    !reference.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix) ||
                    !reference.EndsWith(comparisonType: StringComparison.Ordinal, value: suffix)) { continue; }
                source = source.Replace(oldValue: "*", newValue: reference.Substring(startIndex: prefix.Length, length: ((reference.Length - prefix.Length) - suffix.Length)), comparisonType: StringComparison.Ordinal);
            }
            counterparts.Add(item: source[11..]);
        }
        if (counterparts.Count == 1) { return counterparts.Single(); }
        warnings.Add(item: $"{reference}: fetch mappings do not identify one local integration branch; removal is blocked.");
        return null;
    }
    private static List<Worktree> ReadWorktrees(string repository, List<string> warnings) {
        var listing = Read(repository: repository, arguments: ["worktree", "list", "--porcelain", "-z"], warnings: warnings);
        var worktrees = new List<Worktree>();
        string? path = null, branch = null, locked = null, prunable = null;
        var head = string.Empty;

        foreach (var field in listing.Split(separator: '\0')) {
            if (field.Length == 0) {
                if (path is not null) {
                    worktrees.Add(item: new Worktree(Branch: branch, Head: head, Locked: locked, Path: path, Prunable: prunable));
                }
                path = branch = locked = prunable = null;
                head = string.Empty;
            } else if (field.StartsWith(comparisonType: StringComparison.Ordinal, value: "worktree ")) {
                path = PuckPaths.Normalize(path: field[9..]);
            } else if (field.StartsWith(comparisonType: StringComparison.Ordinal, value: "HEAD ")) {
                head = field[5..];
            } else if (field.StartsWith(comparisonType: StringComparison.Ordinal, value: "branch refs/heads/")) {
                branch = field[18..];
            } else if ((field == "locked") || field.StartsWith(comparisonType: StringComparison.Ordinal, value: "locked ")) {
                locked = ((field.Length == 6) ? string.Empty : field[7..]);
            } else if ((field == "prunable") || field.StartsWith(comparisonType: StringComparison.Ordinal, value: "prunable ")) {
                prunable = ((field.Length == 8) ? string.Empty : field[9..]);
            }
        }
        return worktrees;
    }
    private static Dirty ReadStatus(string repository) {
        var fields = Read(repository: repository, arguments: ["status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignore-submodules=none"]).Split(separator: '\0');
        var modified = 0;
        var untracked = 0;

        for (var index = 0; (index < fields.Length); ++index) {
            if (fields[index].Length == 0) { continue; }
            switch (fields[index][0]) {
                case '1':
                case 'u':
                    ++modified;
                    break;
                case '2':
                    ++modified;
                    ++index; // A rename or copy has a second NUL-delimited pathname, not another status record.
                    break;
                case '?':
                    ++untracked;
                    break;
                default:
                    throw new InvalidOperationException(message: "git status returned an unrecognized porcelain record.");
            }
        }
        return new Dirty(Modified: modified, Untracked: untracked);
    }
    private static HashSet<string> PatchIds(string repository, string patch) => Read(
        repository: repository,
        arguments: ["patch-id", "--stable"],
        input: patch
    ).Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n')
        .Select(selector: static line => line.Split(separator: ' ')[0]).ToHashSet(comparer: StringComparer.Ordinal);
    private static bool SquashEquivalent(string repository, string into, string head, Dictionary<string, HashSet<string>> integrationPatches) {
        var mergeBase = Git(repository: repository, arguments: ["merge-base", into, head]);

        if (mergeBase.ExitCode == 1) { return false; }
        if (mergeBase.ExitCode != 0) { throw new InvalidOperationException(message: Failure(operation: "merge-base", result: mergeBase)); }
        var basis = mergeBase.Stdout.Trim();
        var combined = PatchIds(repository: repository, patch: Read(repository: repository, arguments: ["diff", .. DiffOptions, basis, head, "--"]));

        if (combined.Count != 1) { return false; }
        if (!integrationPatches.TryGetValue(key: basis, value: out var landed)) {
            landed = PatchIds(repository: repository, patch: Read(
                repository: repository,
                arguments: ["log", "--format=commit %H", "--no-decorate", "-p", "--diff-merges=first-parent", .. DiffOptions, $"{basis}..{into}", "--"]
            ));
            integrationPatches.Add(key: basis, value: landed);
        }
        return combined.Overlaps(other: landed);
    }
    private static History ReadHistory(string repository, string into, string head, Dictionary<string, HashSet<string>> integrationPatches) {
        DateTimeOffset? date = null;
        int? unlanded = null;

        try {
            date = DateTimeOffset.Parse(input: Read(repository: repository, arguments: ["show", "--no-patch", "--format=%cI", head, "--"]).Trim(), formatProvider: CultureInfo.InvariantCulture);
            var ancestry = Git(repository: repository, arguments: ["merge-base", "--is-ancestor", head, into]);

            if (ancestry.ExitCode == 0) { return new History(Landed: "ancestor", LastCommit: date, Unlanded: 0, Unreadable: null); }
            if (ancestry.ExitCode != 1) { throw new InvalidOperationException(message: Failure(operation: "merge-base --is-ancestor", result: ancestry)); }
            var cherry = Read(repository: repository, arguments: ["cherry", into, head]).Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n');

            if (cherry.Any(predicate: static line => (!line.StartsWith(comparisonType: StringComparison.Ordinal, value: "+ ") && !line.StartsWith(comparisonType: StringComparison.Ordinal, value: "- ")))) {
                throw new InvalidOperationException(message: "git cherry returned an unrecognized record.");
            }
            unlanded = cherry.Count(predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "+ "));
            // Cherry omits merge commits, including work introduced while resolving a merge.
            var hasMerges = ((unlanded == 0) && (Read(repository: repository, arguments: ["rev-list", "--merges", $"{into}..{head}", "--"]).Length != 0));
            var landed = (((unlanded == 0) && !hasMerges) ? "patch-equivalent" : (SquashEquivalent(head: head, integrationPatches: integrationPatches, into: into, repository: repository) ? "squash-equivalent" : "no"));

            return new History(Landed: landed, LastCommit: date, Unlanded: unlanded, Unreadable: null);
        } catch (Exception error) when ((error is InvalidOperationException or FormatException)) {
            return new History(Landed: "no", Unlanded: unlanded, LastCommit: date, Unreadable: error.Message);
        }
    }
    private static Entry Inspect(string? into, Branch? branch, Worktree? worktree, Worktree? main, History history, DateTimeOffset now, string? listingError) {
        Dirty? dirty = null;
        var unreadable = (listingError ?? history.Unreadable);

        if (worktree is not null) {
            try {
                if (!Directory.Exists(path: worktree.Path)) {
                    throw new InvalidOperationException(message: "worktree directory is missing or inaccessible.");
                }
                var top = Read(repository: worktree.Path, arguments: ["rev-parse", "--show-toplevel"]).TrimEnd('\r', '\n');

                if (!PuckPaths.Comparer.Equals(x: PuckPaths.Normalize(path: top), y: worktree.Path)) {
                    throw new InvalidOperationException(message: "directory is no longer the registered worktree.");
                }
                dirty = ReadStatus(repository: worktree.Path);
            } catch (Exception error) when ((error is InvalidOperationException or IOException or UnauthorizedAccessException)) {
                unreadable = ((unreadable is null) ? error.Message : $"{unreadable}; {error.Message}");
            }
        }
        var name = (branch?.Name ?? worktree?.Branch);
        var blockers = new List<string>();

        if (history.Landed == "no") { blockers.Add(item: "unlanded"); }
        if (dirty is { Modified: > 0 } or { Untracked: > 0 }) { blockers.Add(item: "dirty"); }
        if (worktree?.Locked is not null) { blockers.Add(item: "locked"); }
        if ((worktree is not null) && (main is not null) && PuckPaths.Comparer.Equals(x: worktree.Path, y: main.Path)) { blockers.Add(item: "main-worktree"); }
        if ((name is not null) && (name == into)) { blockers.Add(item: "integration-branch"); }
        if ((name is not null) && (name == main?.Branch)) { blockers.Add(item: "main-worktree-branch"); }
        if (unreadable is not null) { blockers.Add(item: "unreadable"); }
        return new Entry {
            Branch = name,
            Worktree = worktree?.Path,
            Head = (worktree?.Head ?? branch!.Head),
            Landed = history.Landed,
            Unlanded = history.Unlanded,
            Dirty = dirty,
            LastCommit = history.LastCommit,
            AgeDays = ((history.LastCommit is { } date) ? Math.Max(val1: 0, val2: ((long)(now - date).TotalDays)) : null),
            Upstream = branch?.Upstream,
            Locked = worktree?.Locked,
            Prunable = worktree?.Prunable,
            Unreadable = unreadable,
            Blockers = blockers,
        };
    }

    /// <summary>Writes one JSON report of local branches and worktrees. Git reads disable optional writes and lazy fetching.</summary>
    /// <param name="repositoryRoot">A directory in the repository to inspect.</param>
    /// <param name="into">The integration branch: a local branch name, or a remote-tracking one such as <c>origin/main</c>.</param>
    /// <param name="clock">The clock used to compute whole commit ages.</param>
    /// <returns>Zero when a report is produced, including unreadable entries; two when <paramref name="into"/> names no local or remote-tracking branch, or the branch inventory is unreadable.</returns>
    public static int Execute(string repositoryRoot, string into, TimeProvider clock) {
        var errors = new List<string>();
        Branch[] branches;

        try {
            branches = ReadBranches(repository: repositoryRoot, warnings: errors);
        } catch (InvalidOperationException error) {
            return CliExit.Refuse(verb: Verb, what: "local branches", why: error.Message);
        }
        var named = branches.FirstOrDefault(predicate: branch => (branch.Name == into));
        var integration = (named?.Head ?? string.Empty);
        var local = named?.Name;

        if (named is null) {
            try {
                // Ref inventories match exact names; revision parsing also accepts expressions and tag impostors.
                var reference = $"refs/remotes/{into}";
                var remote = Read(repository: repositoryRoot, warnings: errors, arguments: ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(symref)", "refs/remotes/"])
                    .Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n')
                    .Select(selector: static line => line.TrimEnd(trimChar: '\r').Split(separator: '\0'))
                    .FirstOrDefault(predicate: fields => (fields[0] == reference));

                if (remote is null) { return CliExit.Refuse(verb: Verb, what: into, why: "names no local or remote-tracking branch."); }
                integration = remote[1];
                local = LocalCounterpart(repository: repositoryRoot, reference: ((remote[2].Length == 0) ? reference : remote[2]), warnings: errors);
            } catch (InvalidOperationException error) {
                return CliExit.Refuse(verb: Verb, what: into, why: error.Message);
            }
        }
        List<Worktree> worktrees;

        try {
            worktrees = ReadWorktrees(repository: repositoryRoot, warnings: errors);
        } catch (InvalidOperationException error) {
            errors.Add(item: error.Message);
            worktrees = [];
        }
        var main = worktrees.FirstOrDefault();
        var now = clock.GetUtcNow();
        var histories = new Dictionary<string, History>(comparer: StringComparer.Ordinal);
        var integrationPatches = new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal);
        var entries = new List<Entry>();

        void Add(Branch? branch, Worktree? worktree) {
            var head = (worktree?.Head ?? branch!.Head);

            if (!histories.TryGetValue(key: head, value: out var history)) {
                history = ReadHistory(head: head, integrationPatches: integrationPatches, into: integration, repository: repositoryRoot);
                histories.Add(key: head, value: history);
            }
            entries.Add(item: Inspect(into: local, branch: branch, worktree: worktree, main: main, history: history, now: now, listingError: errors.FirstOrDefault()));
        }
        foreach (var branch in branches) {
            var joined = worktrees.Where(predicate: worktree => (worktree.Branch == branch.Name)).ToArray();

            if (joined.Length == 0) { Add(branch: branch, worktree: null); }
            foreach (var worktree in joined) { Add(branch: branch, worktree: worktree); }
        }
        var names = branches.Select(selector: static branch => branch.Name).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var worktree in worktrees.Where(predicate: worktree => ((worktree.Branch is null) || !names.Contains(item: worktree.Branch)))) {
            Add(branch: null, worktree: worktree);
        }
        Console.Out.WriteLine(value: JsonSerializer.Serialize(value: new {
            into,
            entries = entries.OrderBy(keySelector: static entry => (entry.Branch ?? entry.Worktree), comparer: StringComparer.Ordinal).ThenBy(keySelector: static entry => entry.Worktree, comparer: StringComparer.Ordinal),
            errors,
        }, options: JsonOptions));
        return CliExit.Success;
    }
    /// <summary>Creates the report-only command.</summary>
    /// <param name="clock">The CLI host's clock; system time for the real invocation.</param>
    /// <returns>The command.</returns>
    public static Command Create(TimeProvider clock) {
        var into = new Option<string>(name: "--into") { Description = "The exact integration branch name, local first or remote-tracking (origin/main); required, with no default.", Required = true };
        var command = new Command(description: "Report which local branches and worktrees are safe to remove.", name: Verb) { into };

        command.Detail(detail: """
            Report-only: writes one indented JSON document to stdout. Never deletes, prunes,
            fetches, pushes, or writes refs, objects, configuration, or indexes; never contacts
            a remote. Upstream names and tracking state are the locally recorded values.

            Landed is ancestor when HEAD is an ancestor of --into; otherwise patch-equivalent
            when git cherry has no '+' commits and there are no unlanded merge commits;
            otherwise squash-equivalent when the stable patch id of the combined diff
            from merge base to HEAD matches one commit on
            merge-base..into; otherwise no. Unlanded counts '+' commits even for a squash.

            Removal also requires a clean, readable tree, no lock, and neither the main
            worktree, its branch, nor the integration branch (origin/main protects main).
            Remote counterparts use locally configured fetch mappings, including slash-containing
            remote names; symbolic remote refs use their target. Missing or ambiguous counterpart
            mappings block all removal and appear in errors. Exact local names take precedence
            over remote-tracking names; tags and revision expressions are refused.
            Unreadable entries stay listed with reasons. Branches are joined to their
            worktrees; detached trees stand alone.
            Entries sort by branch name (worktree path when detached), ordinally.
            Exit 0 when the report is produced; exit 2 when --into names no local or
            remote-tracking branch, or for an unreadable branch inventory. Missing --into
            is a usage refusal.
            """);
        command.SetAction(action: result => Execute(repositoryRoot: ".", into: result.GetRequiredValue(option: into), clock: clock));
        return command;
    }
}
