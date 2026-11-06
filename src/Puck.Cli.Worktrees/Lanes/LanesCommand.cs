using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Puck.Cli.Lanes;

/// <summary>
/// <c>puck lanes</c>: one line per worktree for the lead, read without walking any worktree's tree: git state, the age
/// of the newest write, the lane's log, its lease state and a stall mark. Report-only; it changes no file, index or lease.
/// </summary>
public static partial class LanesCommand {
    private const string Verb = "lanes";
    private const int ChangedFileCap = 500;
    private const int ScratchEntryCap = 2000;
    private const int LogTailBytes = 8192;

    private static readonly string[] Leases = ["gpu", "heavy", "build"];
    private static readonly JsonSerializerOptions JsonOptions = new() { NewLine = "\n", PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly string[] GitPrefix = ["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "maintenance.auto=false", "-c", "gc.auto=0"];

    private const int LogHeadBytes = 4096;
    private const string WriteMode = "write";
    private const string FindingsMode = "findings-only";

    [GeneratedRegex(pattern: @"^(codex|agy) exit (-?\d+)$")]
    private static partial Regex ExitLine();
    // Codex prints its sandbox in the header of every exec run: "sandbox: read-only".
    [GeneratedRegex(pattern: @"^sandbox: read-only\b", options: RegexOptions.Multiline)]
    private static partial Regex ReadOnlySandbox();

    /// <summary>One lane's use of a lease.</summary>
    /// <param name="Resource">The lease: gpu, heavy or build.</param>
    /// <param name="State">holding or queued.</param>
    /// <param name="Ticket">The queue ticket, for a queued lane.</param>
    /// <param name="Position">The lane's turn among the queued lanes in the round, for a queued lane.</param>
    /// <param name="Lanes">How many lanes are queued, for a queued lane.</param>
    /// <param name="Ahead">How many admissions come before the lane's, for a queued lane.</param>
    public sealed record LeaseUse(string Resource, string State, string? Ticket, int? Position, int? Lanes, int? Ahead) {
        /// <summary>Renders the use as one token group, such as <c>gpu:holding</c> or <c>gpu:queued#5(2/3)</c>.</summary>
        /// <returns>The display text.</returns>
        public override string ToString() => ((State == "holding") ? $"{Resource}:holding" : $"{Resource}:queued#{Ticket}({Position}/{Lanes},ahead {Ahead})");
    }

    /// <summary>One worktree's state.</summary>
    /// <param name="Name">The worktree directory's name.</param>
    /// <param name="Branch">The checked-out branch, or <c>(detached)</c>.</param>
    /// <param name="Path">The worktree's forward-slashed path.</param>
    /// <param name="Main">Whether this is the main checkout.</param>
    /// <param name="Ahead">Commits ahead of the base, or null when the base does not resolve.</param>
    /// <param name="Staged">Paths with a staged change.</param>
    /// <param name="Unstaged">Paths with an unstaged change.</param>
    /// <param name="Files">Distinct paths with a staged or unstaged change.</param>
    /// <param name="CommitAt">The last commit's committer time.</param>
    /// <param name="Subject">The last commit's subject.</param>
    /// <param name="Brief">The last Brief trailer between the base and HEAD.</param>
    /// <param name="WriteAt">The newest write among the index, the worktree's scratch directory, the changed files and the lane's log.</param>
    /// <param name="LogAt">When the lane's log was last written.</param>
    /// <param name="LogExit">The log's final <c>codex exit N</c> or <c>agy exit N</c> line, when the log ends with one.</param>
    /// <param name="Leases">The leases the lane holds or waits for.</param>
    /// <param name="Mode">write, or findings-only for a lane that delivers a report instead of commits: its task file
    /// says so or its log's Codex header names a read-only sandbox.</param>
    /// <param name="Report">The findings-only lane's delivered report, a non-empty file, or null.</param>
    /// <param name="State">active; done or blocked (the Brief trailer); reported (a findings-only lane's report is
    /// delivered); exited (the log ends with an exit line and nothing was delivered); main for the main checkout.</param>
    /// <param name="Stall">Whether an active lane shows no progress for the stall interval while neither holding nor
    /// waiting for a lease: no write for a writer, and no log growth for a findings-only lane whose log has output.</param>
    /// <param name="Error">Why part of the row could not be read.</param>
    public sealed record LaneRow(
        string Name,
        string Branch,
        string Path,
        bool Main,
        int? Ahead,
        int Staged,
        int Unstaged,
        int Files,
        DateTimeOffset? CommitAt,
        string Subject,
        string? Brief,
        DateTimeOffset? WriteAt,
        DateTimeOffset? LogAt,
        string? LogExit,
        IReadOnlyList<LeaseUse> Leases,
        string Mode,
        string? Report,
        string State,
        bool Stall,
        string? Error
    );

    private sealed record Worktree(string Path, string? Branch, string Head);

    private static async Task<(int ExitCode, string Stdout)> GitAsync(string repository, CancellationToken cancellationToken, params string[] arguments) {
        var run = await CliGit.RunAsync(arguments: [.. GitPrefix, .. arguments], cancellationToken: cancellationToken, repository: repository, timeout: TimeSpan.FromSeconds(seconds: 30)).ConfigureAwait(continueOnCapturedContext: false);

        return (run.ExitCode, run.Stdout);
    }

    private static List<Worktree> ParseWorktrees(string listing) {
        var worktrees = new List<Worktree>();
        string? path = null, branch = null;
        var head = string.Empty;

        foreach (var field in listing.Split(separator: '\0')) {
            if (field.Length == 0) {
                if (path is not null) { worktrees.Add(item: new Worktree(Branch: branch, Head: head, Path: path)); }
                path = branch = null;
                head = string.Empty;
            } else if (field.StartsWith(comparisonType: StringComparison.Ordinal, value: "worktree ")) {
                path = field[9..].Replace(oldChar: '\\', newChar: '/');
            } else if (field.StartsWith(comparisonType: StringComparison.Ordinal, value: "HEAD ")) {
                head = field[5..];
            } else if (field.StartsWith(comparisonType: StringComparison.Ordinal, value: "branch refs/heads/")) {
                branch = field[18..];
            }
        }
        if (path is not null) { worktrees.Add(item: new Worktree(Branch: branch, Head: head, Path: path)); }
        return worktrees;
    }

    private static DateTimeOffset? Stamp(string path) {
        try {
            var time = File.GetLastWriteTimeUtc(path: path);

            return ((time.Year > 1601) ? new DateTimeOffset(dateTime: time) : null);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
    private static DateTimeOffset? Newest(DateTimeOffset? a, DateTimeOffset? b) => ((a is null) ? b : ((b is null) ? a : ((a > b) ? a : b)));

    /// <summary>Resolves a worktree's git directory from its <c>.git</c> entry.</summary>
    private static string? GitDirectory(string worktree) {
        var entry = System.IO.Path.Combine(path1: worktree, path2: ".git");

        if (Directory.Exists(path: entry)) { return entry; }
        try {
            var line = File.ReadLines(path: entry).FirstOrDefault();

            if ((line is not null) && line.StartsWith(comparisonType: StringComparison.Ordinal, value: "gitdir: ")) {
                var target = line[8..].Trim();

                return (System.IO.Path.IsPathRooted(path: target) ? target : System.IO.Path.GetFullPath(basePath: worktree, path: target));
            }
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>The newest top-level write under a worktree's scratch directory: its entries only, never their contents.</summary>
    private static DateTimeOffset? ScratchWrite(string worktree) {
        var scratch = System.IO.Path.Combine(path1: worktree, path2: ".claude", path3: "scratch");
        DateTimeOffset? newest = null;

        try {
            var directory = new DirectoryInfo(path: scratch);

            if (!directory.Exists) { return null; }
            newest = new DateTimeOffset(dateTime: directory.LastWriteTimeUtc);
            var seen = 0;

            foreach (var entry in directory.EnumerateFileSystemInfos()) {
                newest = Newest(a: newest, b: new DateTimeOffset(dateTime: entry.LastWriteTimeUtc));
                if (++seen >= ScratchEntryCap) { break; }
            }
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return newest;
    }

    /// <summary>What a lane log says, from its head and tail alone: the final line when it is an exit line, whether
    /// its header names a read-only sandbox, and its length.</summary>
    private static (string? Exit, bool ReadOnly, long Length) ReadLog(string log) {
        try {
            using var stream = new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: log, share: (FileShare.ReadWrite | FileShare.Delete));
            var length = stream.Length;
            var head = new byte[(int)Math.Min(val1: length, val2: LogHeadBytes)];
            var headRead = stream.Read(buffer: head, offset: 0, count: head.Length);
            var readOnly = ReadOnlySandbox().IsMatch(input: Encoding.UTF8.GetString(bytes: head, index: 0, count: headRead).Replace(oldValue: "\r", newValue: string.Empty));

            _ = stream.Seek(offset: Math.Max(val1: 0, val2: (length - LogTailBytes)), origin: SeekOrigin.Begin);
            var buffer = new byte[(int)Math.Min(val1: length, val2: LogTailBytes)];
            var read = stream.Read(buffer: buffer, offset: 0, count: buffer.Length);
            var last = Encoding.UTF8.GetString(bytes: buffer, index: 0, count: read).Split(separator: '\n').Select(selector: static line => line.Trim()).LastOrDefault(predicate: static line => (line.Length > 0));

            return ((((last is not null) && ExitLine().IsMatch(input: last)) ? last : null), readOnly, length);
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return (null, false, 0); }
    }

    /// <summary>Reads a lane's task file, <c>.claude/scratch/&lt;lane&gt;.task</c>: <c>key: value</c> lines, of which
    /// <c>mode</c> (write or findings-only) and <c>report</c> (where a findings-only lane delivers) are read.</summary>
    private static Dictionary<string, string> ReadTask(string path) {
        var fields = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);

        try {
            foreach (var line in File.ReadLines(path: path)) {
                var colon = line.IndexOf(value: ':');

                if (colon > 0) { fields[line[..colon].Trim()] = line[(colon + 1)..].Trim(); }
            }
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return fields;
    }
    private static bool Delivered(string path) {
        try {
            var file = new FileInfo(fileName: path);

            return (file.Exists && (file.Length > 0));
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static string BriefState(string? headBrief) {
        var words = (headBrief ?? string.Empty).Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries);

        if (words.Length < 2) { return "active"; }
        return words[1].StartsWith(comparisonType: StringComparison.Ordinal, value: "done") ? "done" : (words[1].StartsWith(comparisonType: StringComparison.Ordinal, value: "blocked") ? "blocked" : "active");
    }

    private static async Task<LaneRow> ReadAsync(Worktree worktree, bool main, string mainRoot, string leaseRoot, string baseRef, TimeSpan stall, DateTimeOffset now, CancellationToken cancellationToken) {
        var name = System.IO.Path.GetFileName(path: worktree.Path.TrimEnd(trimChar: '/'));
        var branch = (worktree.Branch ?? "(detached)");
        string? error = null;

        if (!Directory.Exists(path: worktree.Path)) {
            return new LaneRow(Ahead: null, Branch: branch, Brief: null, CommitAt: null, Error: "directory is missing", Files: 0, Leases: [], LogAt: null, LogExit: null, Main: main, Mode: WriteMode, Name: name, Path: worktree.Path, Report: null, State: "missing", Stall: false, Staged: 0, Subject: string.Empty, Unstaged: 0, WriteAt: null);
        }
        var statusTask = GitAsync(worktree.Path, cancellationToken, "status", "--porcelain=v1", "-z", "--untracked-files=no");
        var headTask = GitAsync(worktree.Path, cancellationToken, "log", "-1", "--format=%ct%x1f%s%x1f%(trailers:key=Brief,valueonly,separator=%x2c)");
        var rangeTask = GitAsync(worktree.Path, cancellationToken, "log", $"{baseRef}..HEAD", "--format=%x1e%(trailers:key=Brief,valueonly,separator=%x2c)");
        var status = await statusTask.ConfigureAwait(continueOnCapturedContext: false);
        var head = await headTask.ConfigureAwait(continueOnCapturedContext: false);
        var range = await rangeTask.ConfigureAwait(continueOnCapturedContext: false);
        int staged = 0, unstaged = 0;
        var changed = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (status.ExitCode == 0) {
            var fields = status.Stdout.Split(separator: '\0');

            for (var index = 0; index < fields.Length; index++) {
                var field = fields[index];

                if (field.Length < 4) { continue; }
                if (field[0] is not (' ' or '?')) { staged++; }
                if (field[1] is not (' ' or '?')) { unstaged++; }
                _ = changed.Add(item: field[3..]);
                if (field[0] is 'R' or 'C') { index++; }
            }
        } else { error = "git status failed"; }
        DateTimeOffset? commitAt = null;
        var subject = string.Empty;
        string? headBrief = null;

        if (head.ExitCode == 0) {
            var parts = head.Stdout.TrimEnd().Split(separator: '\u001f');

            if ((parts.Length >= 2) && long.TryParse(s: parts[0], provider: CultureInfo.InvariantCulture, result: out var seconds)) { commitAt = DateTimeOffset.FromUnixTimeSeconds(seconds: seconds); }
            if (parts.Length >= 2) { subject = parts[1]; }
            if ((parts.Length >= 3) && (parts[2].Trim().Length > 0)) { headBrief = parts[2].Trim(); }
        } else { error ??= "git log failed"; }
        int? ahead = null;
        string? brief = null;

        if (range.ExitCode == 0) {
            var records = range.Stdout.Split(separator: '\u001e', options: StringSplitOptions.None).Skip(count: 1).ToArray();

            ahead = records.Length;
            brief = records.Select(selector: static record => record.Trim()).FirstOrDefault(predicate: static record => (record.Length > 0));
        }
        var gitDirectory = GitDirectory(worktree: worktree.Path);
        var writeAt = ScratchWrite(worktree: worktree.Path);

        if (gitDirectory is not null) {
            writeAt = Newest(a: writeAt, b: Stamp(path: System.IO.Path.Combine(path1: gitDirectory, path2: "index")));
            writeAt = Newest(a: writeAt, b: Stamp(path: System.IO.Path.Combine(path1: gitDirectory, path2: "logs", path3: "HEAD")));
        }
        foreach (var path in changed.Take(count: ChangedFileCap)) { writeAt = Newest(a: writeAt, b: Stamp(path: System.IO.Path.Combine(path1: worktree.Path, path2: path))); }
        DateTimeOffset? logAt = null;
        string? logExit = null;
        var logLength = 0L;
        var readOnlyLog = false;
        string? declaredMode = null;
        string? reportPath = null;
        var scratchRoots = new[] { worktree.Path, mainRoot }.Distinct(comparer: StringComparer.OrdinalIgnoreCase).Select(selector: static root => System.IO.Path.Combine(path1: root, path2: ".claude", path3: "scratch")).ToArray();

        foreach (var scratch in scratchRoots) {
            foreach (var candidate in LogNames(branch: worktree.Branch, name: name)) {
                var log = System.IO.Path.Combine(path1: scratch, path2: (candidate + ".log"));
                var stamp = Stamp(path: log);

                if ((stamp is not null) && File.Exists(path: log) && ((logAt is null) || (stamp > logAt))) {
                    var read = ReadLog(log: log);

                    logAt = stamp;
                    logExit = read.Exit;
                    logLength = read.Length;
                    readOnlyLog = read.ReadOnly;
                }
                var task = ReadTask(path: System.IO.Path.Combine(path1: scratch, path2: (candidate + ".task")));

                if (task.TryGetValue(key: "mode", value: out var declared) && (declared is WriteMode or FindingsMode)) { declaredMode ??= declared; }
                if (task.TryGetValue(key: "report", value: out var declaredReport) && (declaredReport.Length > 0)) { reportPath ??= System.IO.Path.GetFullPath(basePath: worktree.Path, path: declaredReport); }
            }
        }
        writeAt = Newest(a: writeAt, b: logAt);
        var mode = (declaredMode ?? (readOnlyLog ? FindingsMode : WriteMode));
        string? report = null;

        if (mode == FindingsMode) {
            var reports = ((reportPath is { } declaredPath) ? new[] { declaredPath } : scratchRoots.SelectMany(selector: scratch => LogNames(branch: worktree.Branch, name: name).Select(selector: candidate => System.IO.Path.Combine(path1: scratch, path2: (candidate + "-findings.md")))));

            report = reports.FirstOrDefault(predicate: Delivered)?.Replace(oldChar: '\\', newChar: '/');
        }
        var names = LaneNames(branch: worktree.Branch, name: name);
        // A holder or waiter that recorded its checkout belongs to the worktree at that path, whatever it is called; only
        // one whose CLI recorded none is matched by name.
        bool Mine(string? checkout, string? lane) => ((checkout is not null) ? CliPaths.Same(left: checkout, right: worktree.Path) : ((lane is not null) && names.Contains(item: lane)));
        var uses = new List<LeaseUse>();

        foreach (var resource in Leases) {
            var state = CliLease.Inspect(name: resource, now: now, root: leaseRoot);

            if (state.HolderAlive && Mine(checkout: state.Checkout, lane: state.Lane)) { uses.Add(item: new LeaseUse(Ahead: null, Lanes: null, Position: null, Resource: resource, State: "holding", Ticket: null)); }
            foreach (var ticket in state.Queue.Where(predicate: ticket => Mine(checkout: ticket.Checkout, lane: ticket.Lane))) {
                uses.Add(item: new LeaseUse(Ahead: ticket.Ahead, Lanes: ticket.Lanes, Position: ticket.Position, Resource: resource, State: "queued", Ticket: ticket.Id));
                break;
            }
        }
        // A delivered result outranks the process's exit: a lane that committed its Brief trailer or wrote its report
        // and then exited is finished, while one that exited with neither handed nothing back.
        var delivered = BriefState(headBrief: headBrief);
        var lifecycle = (main ? "main" : ((delivered != "active") ? delivered : ((report is not null) ? "reported" : ((logExit is not null) ? "exited" : "active"))));
        // Progress depends on the task: a writer writes, while a findings-only lane writes nothing until it reports and
        // shows progress only through its log, if its tool logs as it goes.
        var progressAt = ((mode == FindingsMode) ? ((logLength > 0) ? logAt : null) : writeAt);
        var stalled = ((lifecycle == "active") && (uses.Count == 0) && (progressAt is { } progressed) && ((now - progressed) >= stall));

        return new LaneRow(Ahead: ahead, Branch: branch, Brief: brief, CommitAt: commitAt, Error: error, Files: changed.Count, Leases: uses, LogAt: logAt, LogExit: logExit, Main: main, Mode: mode, Name: name, Path: worktree.Path, Report: report, State: lifecycle, Stall: stalled, Staged: staged, Subject: subject, Unstaged: unstaged, WriteAt: writeAt);
    }

    private static IEnumerable<string> LogNames(string? branch, string name) => new[] { name, LaneName(branch: branch) }.Where(predicate: static value => !string.IsNullOrEmpty(value: value)).Distinct(comparer: StringComparer.OrdinalIgnoreCase)!;
    /// <summary>The lane name a branch implies: its last path segment, without the worktree agent prefix.</summary>
    private static string LaneName(string? branch) {
        if (branch is null) { return string.Empty; }
        var tail = branch[(branch.LastIndexOf(value: '/') + 1)..];

        return (tail.StartsWith(comparisonType: StringComparison.Ordinal, value: "worktree-") ? tail[9..] : tail);
    }
    private static HashSet<string> LaneNames(string? branch, string name) => new([name, branch ?? string.Empty, LaneName(branch: branch)], comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads every worktree of the repository.</summary>
    /// <param name="repository">Any worktree of the repository.</param>
    /// <param name="leaseRoot">The lease directory, or null for the main checkout's.</param>
    /// <param name="baseRef">The integration branch commits ahead are counted from.</param>
    /// <param name="stall">How long an active lane may go without a write before it is marked stalled.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>One row per worktree, the main checkout first and the rest by name.</returns>
    public static async Task<IReadOnlyList<LaneRow>> SnapshotAsync(string repository, string? leaseRoot, string baseRef, TimeSpan stall, DateTimeOffset now, CancellationToken cancellationToken = default) {
        var listing = await GitAsync(repository, cancellationToken, "worktree", "list", "--porcelain", "-z").ConfigureAwait(continueOnCapturedContext: false);

        if (listing.ExitCode != 0) { throw new InvalidOperationException(message: $"{CliPaths.ToDisplay(fullPath: System.IO.Path.GetFullPath(path: repository))} is not a git worktree."); }
        var worktrees = ParseWorktrees(listing: listing.Stdout);
        var mainRoot = worktrees[0].Path;
        var leases = (leaseRoot ?? System.IO.Path.Combine(path1: mainRoot, path2: ".claude", path3: "leases"));
        using var gate = new SemaphoreSlim(initialCount: 6);
        var rows = await Task.WhenAll(tasks: worktrees.Select(selector: async (worktree, index) => {
            await gate.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            try {
                return await ReadAsync(baseRef: baseRef, cancellationToken: cancellationToken, leaseRoot: leases, main: (index == 0), mainRoot: mainRoot, now: now, stall: stall, worktree: worktree).ConfigureAwait(continueOnCapturedContext: false);
            } finally { _ = gate.Release(); }
        })).ConfigureAwait(continueOnCapturedContext: false);

        return [.. rows.OrderBy(keySelector: static row => (row.Main ? 0 : 1)).ThenBy(keySelector: static row => row.Name, comparer: StringComparer.Ordinal)];
    }

    /// <summary>Formats a span as its largest whole unit: <c>45s</c>, <c>12m</c>, <c>3h</c> or <c>2d</c>.</summary>
    /// <param name="age">The elapsed time; negative reads as zero.</param>
    /// <returns>The short age.</returns>
    public static string Age(TimeSpan age) => ((age < TimeSpan.FromMinutes(minutes: 1))
        ? $"{Math.Max(val1: 0, val2: (int)age.TotalSeconds)}s"
        : ((age < TimeSpan.FromHours(hours: 1)) ? $"{(int)age.TotalMinutes}m" : ((age < TimeSpan.FromDays(days: 1)) ? $"{(int)age.TotalHours}h" : $"{(int)age.TotalDays}d")));

    private static string AgeOf(DateTimeOffset? time, DateTimeOffset now) => ((time is null) ? "-" : Age(age: (now - time.Value)));
    private static string Shorten(string text, int length) => ((text.Length <= length) ? text : (text[..(length - 1)] + "…"));
    private static string LeaseText(LaneRow row) => ((row.Leases.Count == 0) ? "-" : string.Join(separator: ',', values: row.Leases));
    private static string StateText(LaneRow row) => (row.Stall ? "STALL" : row.State);

    /// <summary>Renders one row as the line <c>puck lanes</c> prints.</summary>
    /// <param name="row">The lane.</param>
    /// <param name="now">The current time ages are measured against.</param>
    /// <returns>The line.</returns>
    public static string Line(LaneRow row, DateTimeOffset now) {
        var line = new StringBuilder();

        _ = line.Append(string.Create(CultureInfo.InvariantCulture, $"{StateText(row: row),-7} {Shorten(text: row.Name, length: 26),-26} {Shorten(text: row.Branch, length: 34),-34} ahead {((row.Ahead is null) ? "?" : row.Ahead.Value.ToString(provider: CultureInfo.InvariantCulture))} files {row.Files}"));
        _ = line.Append(string.Create(CultureInfo.InvariantCulture, $" commit {AgeOf(time: row.CommitAt, now: now)} \"{Shorten(text: row.Subject, length: 60)}\" write {AgeOf(time: row.WriteAt, now: now)}"));
        if (row.LogAt is not null) { _ = line.Append(string.Create(CultureInfo.InvariantCulture, $" log {AgeOf(time: row.LogAt, now: now)}{((row.LogExit is null) ? string.Empty : (" (" + row.LogExit + ")"))}")); }
        _ = line.Append(string.Create(CultureInfo.InvariantCulture, $" lease {LeaseText(row: row)}"));
        if (row.Mode != WriteMode) { _ = line.Append(string.Create(CultureInfo.InvariantCulture, $" mode {row.Mode}")); }
        if (row.Report is not null) { _ = line.Append(string.Create(CultureInfo.InvariantCulture, $" report {row.Report}")); }
        if (row.Brief is not null) { _ = line.Append(string.Create(CultureInfo.InvariantCulture, $" brief {row.Brief}")); }
        if (row.Error is not null) { _ = line.Append(string.Create(CultureInfo.InvariantCulture, $" error: {row.Error}")); }
        return line.ToString();
    }

    /// <summary>Lists what changed between two snapshots: a lane appearing or leaving, finishing, stalling or resuming, or its lease state changing.</summary>
    /// <param name="previous">The earlier rows.</param>
    /// <param name="current">The later rows.</param>
    /// <param name="now">The current time ages are measured against.</param>
    /// <returns>One message per change, each starting with the lane name; empty when nothing changed.</returns>
    public static IReadOnlyList<string> Changes(IReadOnlyList<LaneRow> previous, IReadOnlyList<LaneRow> current, DateTimeOffset now) {
        var before = previous.ToDictionary(keySelector: static row => row.Name, comparer: StringComparer.Ordinal);
        var messages = new List<string>();

        foreach (var row in current) {
            if (!before.Remove(key: row.Name, value: out var old)) {
                messages.Add(item: $"{row.Name}: appeared on {row.Branch}");
                continue;
            }
            if (!row.Main && (old.State == "active") && (row.State != "active")) {
                var undelivered = ((row.State == "exited") ? $"; the process exited without {((row.Mode == FindingsMode) ? "a report" : "a Brief trailer")}" : string.Empty);

                messages.Add(item: $"{row.Name}: finished ({row.State}{((row.LogExit is null) ? string.Empty : (", " + row.LogExit))}{((row.Brief is null) ? string.Empty : (", brief " + row.Brief))}{((row.Report is null) ? string.Empty : (", report " + row.Report))}{undelivered})");
            } else if (!row.Main && (old.State != "active") && (row.State == "active")) {
                messages.Add(item: $"{row.Name}: active again");
            }
            if (row.Stall && !old.Stall) { messages.Add(item: ((row.Mode == FindingsMode) ? $"{row.Name}: STALL, no log output for {AgeOf(time: row.LogAt, now: now)}" : $"{row.Name}: STALL, no write for {AgeOf(time: row.WriteAt, now: now)}")); }
            if (!row.Stall && old.Stall && (row.State == "active")) { messages.Add(item: $"{row.Name}: no longer stalled"); }
            if (LeaseText(row: row) != LeaseText(row: old)) { messages.Add(item: $"{row.Name}: lease {LeaseText(row: old)} -> {LeaseText(row: row)}"); }
        }
        foreach (var gone in before.Values) { messages.Add(item: $"{gone.Name}: removed"); }
        return messages;
    }

    /// <summary>Prints one snapshot, as lines or as JSON.</summary>
    /// <param name="repository">Any worktree of the repository.</param>
    /// <param name="leaseRoot">The lease directory, or null for the main checkout's.</param>
    /// <param name="baseRef">The integration branch commits ahead are counted from.</param>
    /// <param name="stall">How long an active lane may go without a write before it is marked stalled.</param>
    /// <param name="json">Whether to print the rows as a JSON array.</param>
    /// <param name="clock">The CLI clock.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> ExecuteAsync(string repository, string? leaseRoot, string baseRef, TimeSpan stall, bool json, TimeProvider clock, CancellationToken cancellationToken = default) {
        var now = clock.GetUtcNow();
        var rows = await SnapshotAsync(baseRef: baseRef, cancellationToken: cancellationToken, leaseRoot: leaseRoot, now: now, repository: repository, stall: stall).ConfigureAwait(continueOnCapturedContext: false);

        if (json) {
            Console.Out.WriteLine(value: JsonSerializer.Serialize(options: JsonOptions, value: new { @base = baseRef, now, stallMinutes = stall.TotalMinutes, lanes = rows.Select(selector: row => new { row, writeAgeSeconds = AgeSeconds(time: row.WriteAt, now: now), commitAgeSeconds = AgeSeconds(time: row.CommitAt, now: now), logAgeSeconds = AgeSeconds(time: row.LogAt, now: now) }) }));
        } else {
            foreach (var row in rows) { Console.Out.WriteLine(value: Line(row: row, now: now)); }
        }
        return CliExit.Success;
    }
    private static long? AgeSeconds(DateTimeOffset? time, DateTimeOffset now) => ((time is null) ? null : (long)(now - time.Value).TotalSeconds);

    /// <summary>Polls the lanes and prints only what changes.</summary>
    /// <param name="repository">Any worktree of the repository.</param>
    /// <param name="leaseRoot">The lease directory, or null for the main checkout's.</param>
    /// <param name="baseRef">The integration branch commits ahead are counted from.</param>
    /// <param name="stall">How long an active lane may go without a write before it is marked stalled.</param>
    /// <param name="minutes">How long to watch; zero watches until cancelled.</param>
    /// <param name="interval">The time between polls.</param>
    /// <param name="clock">The CLI clock.</param>
    /// <param name="cancellationToken">Ends the watch.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> WatchAsync(string repository, string? leaseRoot, string baseRef, TimeSpan stall, int minutes, TimeSpan interval, TimeProvider clock, CancellationToken cancellationToken = default) {
        var started = clock.GetTimestamp();
        var previous = await SnapshotAsync(baseRef: baseRef, cancellationToken: cancellationToken, leaseRoot: leaseRoot, now: clock.GetUtcNow(), repository: repository, stall: stall).ConfigureAwait(continueOnCapturedContext: false);

        Console.Out.WriteLine(value: $"lanes: watching {previous.Count} worktrees every {interval.TotalSeconds:0}s for {((minutes == 0) ? "ever" : (minutes + "m"))}; stall {stall.TotalMinutes:0}m; {previous.Count(predicate: static row => (row.State == "active"))} active, {previous.Count(predicate: static row => row.Stall)} stalled");
        while ((minutes == 0) || (clock.GetElapsedTime(startingTimestamp: started) < TimeSpan.FromMinutes(minutes: minutes))) {
            try {
                await Task.Delay(delay: interval, timeProvider: clock, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            } catch (OperationCanceledException) { return CliExit.Success; }
            try {
                var now = clock.GetUtcNow();
                var current = await SnapshotAsync(baseRef: baseRef, cancellationToken: cancellationToken, leaseRoot: leaseRoot, now: now, repository: repository, stall: stall).ConfigureAwait(continueOnCapturedContext: false);

                foreach (var message in Changes(previous: previous, current: current, now: now)) { Console.Out.WriteLine(value: $"{clock.GetLocalNow():HH:mm:ss} {message}"); }
                previous = current;
            } catch (OperationCanceledException) { return CliExit.Success; } catch (Exception error) when (error is InvalidOperationException or TimeoutException) {
                Console.Error.WriteLine(value: $"puck {Verb}: poll failed: {error.Message}");
            }
        }
        return CliExit.Success;
    }

    /// <summary>Creates the lanes command.</summary>
    /// <param name="clock">The CLI clock.</param>
    /// <returns>The command.</returns>
    public static Command Create(TimeProvider clock) {
        var baseOption = new Option<string?>(name: "--base") { Description = "The integration branch commits ahead are counted from; defaults to the branch the main checkout holds." };
        var jsonOption = new Option<bool>(name: "--json") { Description = "Print the rows as one JSON document." };
        var stallOption = new Option<int>(name: "--stall") { DefaultValueFactory = static _ => 30, Description = "Minutes without progress (a writer's write, a findings-only lane's log output) after which an active lane that holds no lease and waits for none is marked STALL." };
        var watchOption = new Option<int?>(name: "--watch") { Description = "Poll for this many minutes (0 until cancelled) and print only what changes: a lane finishing, stalling or resuming, a lease changing, a lane appearing or leaving." };
        var intervalOption = new Option<int>(name: "--interval") { DefaultValueFactory = static _ => 30, Description = "Seconds between polls under --watch." };
        var command = new Command(description: "Print one line per worktree: branch, commits ahead, changes, last commit, newest write, log, lease and stall state.", name: Verb) { baseOption, jsonOption, stallOption, watchOption, intervalOption };

        command.Detail(detail: """
            Use: puck lanes [--base <branch>] [--stall <minutes>] [--json] [--watch <minutes> [--interval <seconds>]]
            Report-only: reads git state through read-only git calls (no optional locks), the lane's
            index and scratch directory entries, its log's tail and the lease directory, and changes
            nothing, the lease queue included. It walks no build output and no source tree.

            One line per worktree, the main checkout first, then by name:
              state name branch ahead N files N commit <age> "<subject>" write <age> log <age> (<exit>) lease <uses> brief <trailer>
            ahead counts commits on the branch not on --base (default: the branch the main checkout holds). files is
            paths with a staged or unstaged change. write is the newest write among the git index,
            the git log of HEAD, the changed files, the worktree's .claude/scratch/ entries and the
            lane's log; the log is .claude/scratch/<name>.log (or <branch>.log) in the worktree or the
            main checkout, and shows its final `codex exit N` / `agy exit N` line. lease lists the
            gpu, heavy and build leases the lane holds (gpu:holding) or waits for
            (gpu:queued#5(2/3,ahead 4): ticket 5, its lane's turn 2 of 3 in the round, 4
            admissions before it), matched by the checkout path the holder or waiter recorded,
            or, for one whose CLI recorded none, by lane name: the worktree name, the branch, or
            the branch's last segment. A --base that names no commit is refused (exit 2).

            mode is write, or findings-only for a lane that delivers a report instead of commits:
            its .claude/scratch/<name>.task file says `mode: findings-only` (and may name
            `report: <path>`), or its log's Codex header names a read-only sandbox. A findings-only
            lane's report is the task's report path, else .claude/scratch/<name>-findings.md in the
            worktree or the main checkout, counted once it is a non-empty file.

            state is what the lane delivered first, then whether its process ended: done or blocked
            (the last commit's Brief trailer), reported (a findings-only lane's report), exited (the
            log ends with an exit line and nothing was delivered), active, main, or STALL: an
            active lane that shows no progress for --stall minutes (default 30) while it neither
            holds nor waits for a lease. A writer's progress is its newest write; a findings-only
            lane writes nothing until it reports, so its progress is its log's growth, and one whose
            log is still empty is never marked.

            --watch <minutes> polls every --interval seconds (default 30) for that long (0 until
            cancelled) and prints one line per change after a first summary line. Run it as one
            background monitor.
            """);
        command.SetAction(action: (result, cancellationToken) => {
            var stall = result.GetValue(option: stallOption);
            var interval = result.GetValue(option: intervalOption);
            var watch = result.GetValue(option: watchOption);

            if (stall < 1) { return Task.FromResult(result: CliExit.Refuse(verb: Verb, what: "--stall", why: "must be at least one minute.")); }
            if (interval < 1) { return Task.FromResult(result: CliExit.Refuse(verb: Verb, what: "--interval", why: "must be at least one second.")); }
            if (watch < 0) { return Task.FromResult(result: CliExit.Refuse(verb: Verb, what: "--watch", why: "must be nonnegative.")); }
            if ((watch is not null) && result.GetValue(option: jsonOption)) { return Task.FromResult(result: CliExit.Refuse(verb: Verb, what: "--json", why: "cannot combine with --watch.")); }
            string baseRef;

            if (result.GetValue(option: baseOption) is { Length: > 0 } given) {
                baseRef = given;
            } else if (!CliGit.TryResolveIntegrationBranch(repository: ".", branch: out baseRef)) {
                return Task.FromResult(result: CliExit.Refuse(verb: Verb, what: "--base", why: "has no default: the main checkout holds no branch; pass the integration branch."));
            }
            if (!CliGit.TryResolveCommit(repository: ".", resolved: out _, revision: baseRef)) {
                return Task.FromResult(result: CliExit.Refuse(verb: Verb, what: $"--base {baseRef}", why: "names no commit in this repository, so no lane's commits ahead could be counted."));
            }
            return ((watch is { } minutes)
                ? WatchAsync(baseRef: baseRef, cancellationToken: cancellationToken, clock: clock, interval: TimeSpan.FromSeconds(seconds: interval), leaseRoot: null, minutes: minutes, repository: ".", stall: TimeSpan.FromMinutes(minutes: stall))
                : ExecuteAsync(baseRef: baseRef, cancellationToken: cancellationToken, clock: clock, json: result.GetValue(option: jsonOption), leaseRoot: null, repository: ".", stall: TimeSpan.FromMinutes(minutes: stall)));
        });
        return command;
    }
}
