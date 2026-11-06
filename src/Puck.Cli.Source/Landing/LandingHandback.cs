using System.CommandLine;
using System.Text.RegularExpressions;

using Puck.Cli.Docs;

namespace Puck.Cli.Landing;

/// <summary>
/// <c>puck landing handback</c> — checks a lane branch's own commits against the hand-back contract the delegate skill
/// states: the <c>Brief:</c> line on the last commit only, an <c>area: sentence</c> subject on every commit the lane
/// wrote, no <c>Co-Authored-By</c> trailer, no commit written by plumbing, and <c>puck docs links</c> clean at the head.
/// </summary>
/// <remarks>The lane's own commits are the first-parent chain from HEAD back to the integration tip (and the branch's
/// creation point, read from its reflog, or <c>--base</c>), less the commits a fast-forward or reset brought onto that
/// chain. A merge of the integration tip keeps the lane as its first parent, so the commits it brings in are never
/// judged as the lane's. Provenance comes from the branch reflog: every commit the lane wrote must be the new value of a
/// <c>commit</c>, <c>merge</c>, <c>cherry-pick</c> or <c>revert</c> entry. A branch with no reflog (one fetched from a
/// remote) cannot show that, and the check says so instead of passing it.</remarks>
public static partial class LandingHandback {
    private const string Verb = "landing handback";
    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    [GeneratedRegex(pattern: @"^Brief:", options: RegexOptions.Multiline)]
    private static partial Regex AnyBriefLine();
    [GeneratedRegex(pattern: @"^Brief: [A-Za-z0-9._-]+ (done|blocked: \S.*)$", options: RegexOptions.Multiline)]
    private static partial Regex BriefLine();
    [GeneratedRegex(pattern: @"^[a-z0-9][a-z0-9._/-]*(, [a-z0-9][a-z0-9._/-]*)*: \S")]
    private static partial Regex Subject();
    [GeneratedRegex(pattern: @"^co-authored-by:", options: (RegexOptions.Multiline | RegexOptions.IgnoreCase))]
    private static partial Regex CoAuthor();
    // The reflog subjects git's porcelain writes when it creates a commit on the branch.
    [GeneratedRegex(pattern: @"^(commit( \([a-z]+\))?|cherry-pick|revert|merge [^:]+: Merge made by)")]
    private static partial Regex AuthoredEntry();
    // The reflog subjects that move a branch onto commits it did not write.
    [GeneratedRegex(pattern: @"^(merge [^:]+: Fast-forward|reset:|pull[^:]*: Fast-forward|checkout:)")]
    private static partial Regex ForeignEntry();
    [GeneratedRegex(pattern: @"^branch: Created from ")]
    private static partial Regex CreationEntry();

    private sealed record LaneCommit(string Hash, IReadOnlyList<string> Parents, string Tree, string Message) {
        public string Subject => Message.Split(separator: '\n', count: 2)[0].TrimEnd(trimChar: '\r');
        public string Label => $"{Hash[..12]} {Subject}";
    }

    public static Command Create() {
        var againstOption = new Option<string>(name: "--against") { Description = "The integration tip the lane lands onto (e.g. lead/fundamentals).", Required = true };
        var baseOption = new Option<string?>(name: "--base") { Description = "The commit the lane branch was created from; defaults to the branch reflog's creation entry." };
        var checkoutOption = new Option<string?>(name: "--checkout") { Description = "The lane's worktree, whose HEAD is the lane tip; defaults to the current directory's checkout." };
        var command = new Command(
            description: "Check a lane branch's commits against the hand-back contract.",
            name: "handback"
        ) { againstOption, baseOption, checkoutOption };

        command.Detail(detail: """
            Use: puck landing handback --against <integration tip> [--base <rev>] [--checkout <lane worktree>]
            Judges the commits the lane wrote: the first-parent chain from the checkout's HEAD back
            to --against and the branch's creation point (its reflog's "branch: Created from" entry,
            or --base), less commits a fast-forward or reset brought onto that chain. A merge of the
            integration tip keeps the lane as first parent, so what it brings in is not judged.

              brief      the final commit, the newest non-merge lane commit, carries "Brief: <id> done"
                         or "Brief: <id> blocked: <question>", no other lane commit carries a Brief:
                         line, and only merges (of the integration tip) follow it.
              subjects   every non-merge lane commit's subject is "area: sentence", the area
                         lowercase (several areas join with ", ").
              coauthor   no lane commit carries a Co-Authored-By trailer.
              plumbing   every lane commit is the new value of a commit, merge, cherry-pick or
                         revert entry in the branch reflog, and none records the empty tree. A
                         branch without a reflog cannot show this and fails the leg as unverifiable.
              docs       the checkout is clean, so its files are HEAD's, and puck docs links passes
                         on the whole corpus there.

            Run it in the lane's worktree after the final commit, or from anywhere with --checkout.
            A finding is fixed on the lane branch: amend the final commit for its subject, trailer or
            docs, and never rewrite history another branch holds.

            Exit codes: 0 every leg passed; 1 a leg failed; 2 invalid input or a git refusal.
            """);
        command.SetAction(action: parseResult => Run(
            against: parseResult.GetRequiredValue(option: againstOption),
            baseRef: parseResult.GetValue(option: baseOption),
            checkout: (parseResult.GetValue(option: checkoutOption) ?? Environment.CurrentDirectory)
        ));
        return command;
    }

    private static string Git(string repository, params string[] arguments) {
        var run = CliGit.Run(arguments: arguments, repository: repository);

        return ((run.ExitCode == 0) ? run.Stdout : throw new InvalidOperationException(message: $"git {string.Join(separator: ' ', values: arguments)} exited {run.ExitCode}: {run.Stderr.Trim()}"));
    }

    /// <summary>Checks the lane branch checked out in <paramref name="checkout"/> against the hand-back contract and
    /// prints one verdict line per leg.</summary>
    /// <param name="checkout">The directory inside the lane's worktree.</param>
    /// <param name="against">The integration tip the lane lands onto.</param>
    /// <param name="baseRef">The commit the branch was created from, or <see langword="null"/> to read it from the
    /// branch reflog.</param>
    /// <returns><see cref="CliExit.Success"/> when every leg passes, <see cref="CliExit.Failed"/> when one fails, and
    /// <see cref="CliExit.Refused"/> for an unknown revision or a checkout git cannot read.</returns>
    public static int Run(string checkout, string against, string? baseRef) {
        var top = CliGit.Run(arguments: ["rev-parse", "--show-toplevel"], repository: checkout);

        if (top.ExitCode != 0) { return CliExit.Refuse(verb: Verb, what: checkout, why: "is not inside a git checkout."); }
        var root = top.Stdout.Trim();

        if (!CliGit.TryResolveCommit(repository: root, resolved: out var head, revision: "HEAD")) { return CliExit.Refuse(verb: Verb, what: root, why: "HEAD names no commit."); }
        if (!CliGit.TryResolveCommit(repository: root, resolved: out var tip, revision: against)) { return CliExit.Refuse(verb: Verb, what: $"--against '{against}'", why: "unknown revision."); }
        if (CliGit.IsAncestor(candidate: head, descendant: tip, repository: root)) { return CliExit.Refuse(verb: Verb, what: head[..12], why: $"HEAD is already on {against}; there is nothing to land."); }

        var branch = CliGit.Run(arguments: ["symbolic-ref", "--short", "-q", "HEAD"], repository: root).Stdout.Trim();
        // Newest first: each entry's hash is the value the branch moved to, the next entry's the value it moved from.
        var reflog = ((branch.Length == 0)
            ? []
            : CliText.Lines(text: CliGit.Run(arguments: ["log", "-g", "--format=%H%x09%gs", $"refs/heads/{branch}", "--"], repository: root).Stdout)
                .Select(selector: static line => line.Split(separator: '\t', count: 2))
                .Where(predicate: static parts => (parts.Length == 2))
                .Select(selector: static parts => (Hash: parts[0], Subject: parts[1]))
                .ToList());
        string? start = null;

        if (baseRef is not null) {
            if (!CliGit.TryResolveCommit(repository: root, resolved: out var resolvedBase, revision: baseRef)) { return CliExit.Refuse(verb: Verb, what: $"--base '{baseRef}'", why: "unknown revision."); }
            start = resolvedBase;
        } else if ((reflog.Count != 0) && CreationEntry().IsMatch(input: reflog[^1].Subject)) {
            start = reflog[^1].Hash;
        }

        var foreign = new HashSet<string>(comparer: StringComparer.Ordinal);
        var authored = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < reflog.Count); index++) {
            var (hash, subject) = reflog[index];

            if (AuthoredEntry().IsMatch(input: subject)) {
                authored.Add(item: hash);
            } else if (ForeignEntry().IsMatch(input: subject) && ((index + 1) < reflog.Count)) {
                foreign.UnionWith(other: CliText.Lines(text: Git(root, "rev-list", hash, $"^{reflog[(index + 1)].Hash}")));
            }
        }

        string[] exclusions = ((start is null) ? [$"^{tip}"] : [$"^{tip}", $"^{start}"]);
        var commits = Git(root, ["log", "--first-parent", "--format=%H%x1f%P%x1f%T%x1f%B%x1e", head, .. exclusions])
            .Split(separator: '\x1e')
            .Select(selector: static record => record.TrimStart(trimChars: ['\r', '\n']))
            .Where(predicate: static record => (record.Length != 0))
            .Select(selector: static record => record.Split(separator: '\x1f', count: 4))
            .Select(selector: static fields => new LaneCommit(Hash: fields[0], Message: fields[3].Replace(oldValue: "\r\n", newValue: "\n").TrimEnd(), Parents: fields[1].Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries), Tree: fields[2]))
            .Where(predicate: commit => !foreign.Contains(item: commit.Hash))
            .ToList();
        var failed = false;

        void Leg(string name, IReadOnlyCollection<string> findings, string pass) {
            if (findings.Count == 0) {
                Console.WriteLine(value: $"{Verb}: {name}: PASS: {pass}");
                return;
            }
            failed = true;
            Console.WriteLine(value: $"{Verb}: {name}: FAIL");
            foreach (var finding in findings) { Console.WriteLine(value: $"    {finding}"); }
        }

        Console.WriteLine(value: $"{Verb}: {((branch.Length == 0) ? "(detached)" : branch)} at {head[..12]} onto {against} ({tip[..12]}), from {((start is null) ? "the tip's merge base" : start[..12])}: {commits.Count} lane commit(s).");
        if (commits.Count == 0) { return CliExit.Refuse(verb: Verb, what: head[..12], why: "the branch holds no commit of its own past --against."); }

        var briefFindings = new List<string>();
        // The final commit is the newest one the lane wrote that is not a merge: a lane merges the integration tip
        // before it reports, and a tip that moves after the final commit puts a merge on top of it.
        var final = commits.FindIndex(match: static commit => (commit.Parents.Count < 2));

        if (commits[0].Hash != head) {
            briefFindings.Add(item: $"HEAD {head[..12]} is not a commit the lane wrote (a fast-forward or reset put it there); end the branch with the lane's own final commit.");
        } else if (final < 0) {
            briefFindings.Add(item: "the lane holds only merges; its final commit carries the Brief: line.");
        } else if (!BriefLine().IsMatch(input: commits[final].Message)) {
            briefFindings.Add(item: $"{commits[final].Label}: the final commit carries no \"Brief: <id> done\" or \"Brief: <id> blocked: <question>\" line.");
        }
        briefFindings.AddRange(collection: commits.Where(predicate: (commit, index) => ((index != final) && AnyBriefLine().IsMatch(input: commit.Message))).Select(selector: static commit => $"{commit.Label}: carries a Brief: line, which belongs on the final commit only."));
        Leg(findings: briefFindings, name: "brief", pass: $"{((final < 0) ? head : commits[final].Hash)[..12]} carries the Brief: line, no other lane commit does, and only merges follow it.");

        Leg(
            findings: [.. commits.Where(predicate: static commit => ((commit.Parents.Count < 2) && !Subject().IsMatch(input: commit.Subject))).Reverse().Select(selector: static commit => $"{commit.Label}: not a lowercase \"area: sentence\" subject.")],
            name: "subjects",
            pass: "every lane commit's subject is \"area: sentence\"."
        );
        Leg(
            findings: [.. commits.Where(predicate: static commit => CoAuthor().IsMatch(input: commit.Message)).Reverse().Select(selector: static commit => $"{commit.Label}: carries a Co-Authored-By trailer.")],
            name: "coauthor",
            pass: "no lane commit carries a Co-Authored-By trailer."
        );

        var plumbing = new List<string>();

        plumbing.AddRange(collection: commits.Where(predicate: static commit => (commit.Tree == EmptyTree)).Select(selector: static commit => $"{commit.Label}: records the empty tree."));
        if (reflog.Count == 0) {
            plumbing.Add(item: $"{((branch.Length == 0) ? "a detached HEAD" : $"refs/heads/{branch}")} has no reflog, so no commit's provenance can be shown; run this where the lane's branch was written.");
        } else {
            plumbing.AddRange(collection: commits.Where(predicate: commit => !authored.Contains(item: commit.Hash)).Reverse().Select(selector: static commit => $"{commit.Label}: no commit, merge, cherry-pick or revert entry in the branch reflog wrote it (plumbing such as commit-tree and update-ref, or a rewritten history)."));
        }
        Leg(findings: plumbing, name: "plumbing", pass: "every lane commit was written by git commit, merge, cherry-pick or revert on this branch.");

        var status = CliGit.Run(arguments: ["status", "--porcelain", "--untracked-files=normal"], repository: root).Stdout;

        if (CliText.Lines(text: status).Any()) {
            Leg(findings: ["the checkout has uncommitted or untracked changes, so its files are not HEAD's; commit or set them aside, then re-run."], name: "docs", pass: string.Empty);
        } else if (!DocsCorpus.TryEnumerate(documents: out var corpus, error: out var corpusError, repositoryRoot: root)) {
            Leg(findings: [corpusError], name: "docs", pass: string.Empty);
        } else {
            var links = DocsLinksCommand.Run(documents: [.. corpus], repositoryRoot: root);

            Leg(findings: ((links == CliExit.Success) ? [] : [$"puck docs links exited {links} at {head[..12]}; its failures are listed above."]), name: "docs", pass: "puck docs links passes at HEAD.");
        }

        return (failed ? CliExit.Failed : CliExit.Success);
    }
}
