using System.Globalization;
using System.Text.Json;
using Puck.Abstractions;
using Puck.Cli.WorktreeReport;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: the worktree report recognizes landed histories, retains every unreadable tree,
/// blocks unsafe removals, and leaves Git's recorded state unchanged.</summary>
public sealed class WorktreeReportCommandLawTests {
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly TimeProvider Clock = new FixedClock(now: new DateTimeOffset(day: 1, hour: 0, minute: 0, month: 1, offset: TimeSpan.Zero, second: 0, year: 2030));

    private static GitScratchCheckout Checkout() {
        var checkout = new GitScratchCheckout();

        _ = checkout.Git("config", "core.longpaths", "true");
        _ = checkout.Git("config", "core.excludesFile", Path.Combine(path1: checkout.Root, path2: ".git/info/exclude"));
        _ = checkout.Git("config", "core.attributesFile", Path.Combine(path1: checkout.Root, path2: ".git/info/attributes"));
        checkout.Write(name: ".gitattributes", text: "* text eol=lf\n");
        checkout.Write(name: "tracked.txt", text: "base\n");
        _ = checkout.Commit(message: "base");
        return checkout;
    }
    private static JsonDocument Report(GitScratchCheckout checkout, TimeProvider? clock = null, string into = "main") {
        var result = ConsoleCapture.RunSplit(run: () => WorktreeReportCommand.Execute(repositoryRoot: checkout.Root, into: into, clock: (clock ?? Clock)));

        Assert.True(condition: (result.ExitCode == 0), userMessage: result.Error);
        Assert.Empty(collection: result.Error);
        var report = JsonDocument.Parse(json: result.Output);

        Assert.Equal(expected: into, actual: report.RootElement.GetProperty(propertyName: "into").GetString());
        Assert.Empty(collection: report.RootElement.GetProperty(propertyName: "errors").EnumerateArray());
        return report;
    }
    private static JsonElement Entry(JsonDocument report, string branch) => Assert.Single(collection: report.RootElement.GetProperty(propertyName: "entries").EnumerateArray(), predicate: entry => (entry.GetProperty(propertyName: "branch").GetString() == branch));
    private static string[] Blockers(JsonElement entry) => entry.GetProperty(propertyName: "blockers").EnumerateArray().Select(selector: static value => value.GetString()!).ToArray();
    private static void Landed(JsonElement entry, string landed, bool removable) {
        Assert.Equal(expected: landed, actual: entry.GetProperty(propertyName: "landed").GetString());
        Assert.Equal(expected: removable, actual: entry.GetProperty(propertyName: "removable").GetBoolean());
    }
    private static string CommitChange(GitScratchCheckout checkout, string name, string text) {
        checkout.Write(name: name, text: text);
        return checkout.Commit(message: name);
    }
    private static string GitAsAuthor(GitScratchCheckout checkout, params string[] arguments) => checkout.Git(["-c", "user.name=law", "-c", "user.email=law@example.invalid", "-c", "commit.gpgsign=false", .. arguments]);

    [Fact]
    public void TwoCommitSquashIsRemovableOnlyAfterLanding() {
        using var checkout = Checkout();

        _ = checkout.Git("checkout", "-b", "squashed");
        _ = CommitChange(checkout: checkout, name: "first.txt", text: "first\n");
        _ = CommitChange(checkout: checkout, name: "second.txt", text: "second\n");
        _ = checkout.Git("checkout", "main");
        using (var before = Report(checkout: checkout)) {
            Landed(entry: Entry(branch: "squashed", report: before), landed: "no", removable: false);
        }
        _ = checkout.Git("merge", "--squash", "squashed");
        _ = checkout.Commit(message: "land both changes together");
        using var after = Report(checkout: checkout);
        var entry = Entry(branch: "squashed", report: after);

        Landed(entry: entry, landed: "squash-equivalent", removable: true);
        Assert.Equal(expected: 2, actual: entry.GetProperty(propertyName: "unlanded").GetInt32());
        Assert.Empty(collection: Blockers(entry: entry));
        Assert.Equal(expected: JsonValueKind.Null, actual: entry.GetProperty(propertyName: "dirty").ValueKind);
    }
    [Fact]
    public void CherryPicksArePatchEquivalentAndMergedHistoryIsAncestor() {
        using var checkout = Checkout();

        _ = checkout.Git("checkout", "-b", "picked");
        var first = CommitChange(checkout: checkout, name: "first.txt", text: "first\n");
        var second = CommitChange(checkout: checkout, name: "second.txt", text: "second\n");

        _ = checkout.Git("checkout", "main");
        _ = CommitChange(checkout: checkout, name: "main.txt", text: "move the parent\n");
        _ = GitAsAuthor(checkout, "cherry-pick", first, second);
        _ = checkout.Git("checkout", "-b", "merged");
        _ = CommitChange(checkout: checkout, name: "merged.txt", text: "merged\n");
        _ = checkout.Git("checkout", "main");
        _ = GitAsAuthor(checkout, "merge", "--no-ff", "--message", "merge", "merged");
        using var report = Report(checkout: checkout);

        Landed(entry: Entry(branch: "picked", report: report), landed: "patch-equivalent", removable: true);
        Landed(entry: Entry(branch: "merged", report: report), landed: "ancestor", removable: true);
        Assert.Equal(expected: 0, actual: Entry(branch: "picked", report: report).GetProperty(propertyName: "unlanded").GetInt32());
    }
    [Fact]
    public void OneUnlandedCommitBlocksRemovalEvenAfterAPartialLanding() {
        using var checkout = Checkout();

        _ = checkout.Git("checkout", "-b", "partial");
        var first = CommitChange(checkout: checkout, name: "first.txt", text: "first\n");

        _ = CommitChange(checkout: checkout, name: "remaining.txt", text: "remaining\n");
        _ = checkout.Git("checkout", "main");
        _ = CommitChange(checkout: checkout, name: "main.txt", text: "different parent\n");
        _ = GitAsAuthor(checkout, "cherry-pick", first);
        using var report = Report(checkout: checkout);
        var entry = Entry(branch: "partial", report: report);

        Landed(entry: entry, landed: "no", removable: false);
        Assert.Equal(expected: 1, actual: entry.GetProperty(propertyName: "unlanded").GetInt32());
        Assert.Equal(expected: new[] { "unlanded" }, actual: Blockers(entry: entry));
    }
    [Fact]
    public void ModifiedAndUntrackedFilesBlockALandedWorktree() {
        using var checkout = Checkout();

        _ = checkout.Git("branch", "dirty");
        var path = checkout.AddWorktree(name: "dirty tree", revision: "dirty");

        File.WriteAllText(path: Path.Combine(path1: path, path2: "tracked.txt"), contents: "modified\n");
        File.WriteAllText(path: Path.Combine(path1: path, path2: "untracked.txt"), contents: "untracked\n");
        using var report = Report(checkout: checkout);
        var entry = Entry(branch: "dirty", report: report);

        Assert.Equal(expected: 1, actual: entry.GetProperty(propertyName: "dirty").GetProperty(propertyName: "modified").GetInt32());
        Assert.Equal(expected: 1, actual: entry.GetProperty(propertyName: "dirty").GetProperty(propertyName: "untracked").GetInt32());
        Landed(entry: entry, landed: "ancestor", removable: false);
        Assert.Equal(expected: new[] { "dirty" }, actual: Blockers(entry: entry));
        Assert.Equal(expected: PuckPaths.Normalize(path: path), actual: entry.GetProperty(propertyName: "worktree").GetString());
        Assert.Equal(expected: 2, actual: report.RootElement.GetProperty(propertyName: "entries").GetArrayLength());
    }
    [Fact]
    public void DetachedWorktreeHasItsOwnEntryAndHeadBasedLanding() {
        using var checkout = Checkout();
        var head = checkout.Git("rev-parse", "HEAD").Trim();
        var path = checkout.AddWorktree(detached: true, name: "detached", revision: head);
        using var report = Report(checkout: checkout);
        var entry = Assert.Single(collection: report.RootElement.GetProperty(propertyName: "entries").EnumerateArray(), predicate: entry => (entry.GetProperty(propertyName: "branch").ValueKind == JsonValueKind.Null));

        Assert.Equal(expected: head, actual: entry.GetProperty(propertyName: "head").GetString());
        Assert.Equal(expected: PuckPaths.Normalize(path: path), actual: entry.GetProperty(propertyName: "worktree").GetString());
        Landed(entry: entry, landed: "ancestor", removable: true);
        Assert.Equal(expected: 2, actual: report.RootElement.GetProperty(propertyName: "entries").GetArrayLength());
    }
    [Fact]
    public void MissingWorktreeStaysListedAndBlocksRemoval() {
        using var checkout = Checkout();

        _ = checkout.Git("branch", "missing");
        var path = checkout.AddWorktree(name: "missing", revision: "missing");

        Directory.Delete(path: path, recursive: true);
        using var report = Report(checkout: checkout);
        var entry = Entry(branch: "missing", report: report);

        Assert.Contains(expectedSubstring: "missing", actualString: entry.GetProperty(propertyName: "unreadable").GetString());
        Assert.False(condition: string.IsNullOrEmpty(value: entry.GetProperty(propertyName: "prunable").GetString()));
        Assert.Equal(expected: JsonValueKind.Null, actual: entry.GetProperty(propertyName: "dirty").ValueKind);
        Landed(entry: entry, landed: "ancestor", removable: false);
        Assert.Equal(expected: new[] { "unreadable" }, actual: Blockers(entry: entry));
        Assert.Equal(expected: 2, actual: report.RootElement.GetProperty(propertyName: "entries").GetArrayLength());
    }
    [Fact]
    public void AgeUsesTheSuppliedClockAndUnknownBranchIsAUsageRefusal() {
        using var checkout = Checkout();
        var date = DateTimeOffset.Parse(input: checkout.Git("show", "--no-patch", "--format=%cI", "HEAD").Trim(), formatProvider: CultureInfo.InvariantCulture);
        using var report = Report(checkout: checkout, clock: new FixedClock(now: date.AddHours(hours: 71)));
        var entry = Entry(branch: "main", report: report);

        Assert.Equal(expected: date, actual: entry.GetProperty(propertyName: "lastCommit").GetDateTimeOffset());
        Assert.Equal(expected: 2, actual: entry.GetProperty(propertyName: "ageDays").GetInt64());
        _ = checkout.Git("tag", "only-a-tag");
        var result = ConsoleCapture.RunSplit(run: () => WorktreeReportCommand.Execute(repositoryRoot: checkout.Root, into: "only-a-tag", clock: Clock));

        Assert.Equal(actual: result.ExitCode, expected: 2);
        Assert.Empty(collection: result.Output);
        Assert.Contains(actualString: result.Error, expectedSubstring: "only-a-tag");
        var missingOption = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: ["worktree-report"]));

        Assert.Equal(actual: missingOption.ExitCode, expected: 2);
        Assert.Empty(collection: missingOption.Output);
        Assert.Contains(actualString: missingOption.Error, expectedSubstring: "--into");
    }

    private static Dictionary<string, (string Bytes, long Written)> Snapshot(string gitDirectory) => Directory.EnumerateFiles(path: gitDirectory, searchOption: SearchOption.AllDirectories, searchPattern: "*")
        .ToDictionary(keySelector: file => Path.GetRelativePath(path: file, relativeTo: gitDirectory), elementSelector: static file => (Convert.ToBase64String(inArray: File.ReadAllBytes(path: file)), File.GetLastWriteTimeUtc(path: file).Ticks), comparer: StringComparer.Ordinal);

    [Fact]
    public void ReportPreservesRefsWorktreeListingStatusAndAllGitFiles() {
        using var checkout = Checkout();

        _ = checkout.Git("checkout", "-b", "landed");
        _ = CommitChange(checkout: checkout, name: "first.txt", text: "first\n");
        _ = CommitChange(checkout: checkout, name: "second.txt", text: "second\n");
        _ = checkout.Git("checkout", "main");
        _ = checkout.Git("merge", "--squash", "landed");
        _ = checkout.Commit(message: "squash");
        var path = checkout.AddWorktree(name: "landed", revision: "landed");

        _ = checkout.Git("remote", "add", "origin", "https://example.invalid/no-contact.git");
        _ = checkout.Git("update-ref", "refs/remotes/origin/landed", "landed");
        _ = checkout.Git("branch", "--set-upstream-to=origin/landed", "landed");
        // Changed timestamps force an index refresh if the report forgets --no-optional-locks.
        File.SetLastWriteTimeUtc(path: Path.Combine(path1: path, path2: "tracked.txt"), lastWriteTimeUtc: DateTime.UtcNow.AddHours(value: -2));
        var refs = checkout.Git("for-each-ref", "--format=%(refname)%00%(objectname)");
        var worktrees = checkout.Git("worktree", "list", "--porcelain", "-z");
        var status = checkout.Git("--no-optional-locks", "status", "--porcelain=v2", "-z");
        var gitDirectory = Path.Combine(path1: checkout.Root, path2: ".git");
        var before = Snapshot(gitDirectory: gitDirectory);
        using var report = Report(checkout: checkout);

        Assert.Equal(expected: refs, actual: checkout.Git("for-each-ref", "--format=%(refname)%00%(objectname)"));
        Assert.Equal(expected: worktrees, actual: checkout.Git("worktree", "list", "--porcelain", "-z"));
        Assert.Equal(expected: status, actual: checkout.Git("--no-optional-locks", "status", "--porcelain=v2", "-z"));
        var after = Snapshot(gitDirectory: gitDirectory);

        Assert.Equal(expected: before.Keys.Order(comparer: StringComparer.Ordinal), actual: after.Keys.Order(comparer: StringComparer.Ordinal));
        foreach (var pair in before) { Assert.Equal(expected: pair.Value, actual: after[pair.Key]); }
        var upstream = Entry(branch: "landed", report: report).GetProperty(propertyName: "upstream");

        Assert.Equal(expected: "origin/landed", actual: upstream.GetProperty(propertyName: "name").GetString());
        Assert.Equal(expected: string.Empty, actual: upstream.GetProperty(propertyName: "track").GetString());
    }
    [Fact]
    public void LocksMainWorktreeAndIntegrationBranchAreProtectedAndNamesSortOrdinally() {
        using var checkout = Checkout();

        _ = checkout.Git("branch", "zeta");
        _ = checkout.Git("branch", "Alpha");
        _ = checkout.Git("branch", "integration");
        var path = checkout.AddWorktree(name: "locked", revision: "Alpha");

        _ = checkout.Git("worktree", "lock", "--reason", "keep for review", path);
        using var report = Report(checkout: checkout, into: "integration");

        Assert.Equal(expected: new[] { "Alpha", "integration", "main", "zeta" }, actual: report.RootElement.GetProperty(propertyName: "entries").EnumerateArray().Select(selector: static entry => entry.GetProperty(propertyName: "branch").GetString()));
        var locked = Entry(branch: "Alpha", report: report);

        Landed(entry: locked, landed: "ancestor", removable: false);
        Assert.Equal(expected: "keep for review", actual: locked.GetProperty(propertyName: "locked").GetString());
        Assert.Equal(expected: new[] { "locked" }, actual: Blockers(entry: locked));
        Assert.Equal(expected: new[] { "integration-branch" }, actual: Blockers(entry: Entry(branch: "integration", report: report)));
        Assert.Equal(expected: new[] { "main-worktree", "main-worktree-branch" }, actual: Blockers(entry: Entry(branch: "main", report: report)));
        Landed(entry: Entry(branch: "zeta", report: report), landed: "ancestor", removable: true);
    }
    [Fact]
    public void ARemoteTrackingIntegrationBranchProtectsItsLocalCounterpartNotItsTrackers() {
        using var checkout = Checkout();

        _ = checkout.Git("checkout", "-b", "lane");
        _ = CommitChange(checkout: checkout, name: "lane.txt", text: "lane\n");
        _ = checkout.Git("checkout", "main");
        _ = checkout.Git("merge", "--squash", "lane");
        _ = checkout.Commit(message: "land the lane");
        _ = checkout.Git("remote", "add", "origin", "https://example.invalid/no-contact.git");
        _ = checkout.Git("update-ref", "refs/remotes/origin/integration", "main");
        _ = checkout.Git("branch", "--track", "integration", "origin/integration");
        // A lane that tracks the integration branch is not the integration branch.
        _ = checkout.Git("branch", "--set-upstream-to=origin/integration", "lane");
        using var report = Report(checkout: checkout, into: "origin/integration");

        Landed(entry: Entry(branch: "lane", report: report), landed: "patch-equivalent", removable: true);
        Assert.Empty(collection: Blockers(entry: Entry(branch: "lane", report: report)));
        Assert.Equal(expected: new[] { "integration-branch" }, actual: Blockers(entry: Entry(branch: "integration", report: report)));
    }
    // A report never lazily fetches: git that can forbid it is told to, and git that cannot reads only a repository
    // with no promisor remote to fetch from.
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [Theory]
    public void OnlyAPartialCloneUnderAGitThatCannotForbidLazyFetchIsRefused(bool gitAcceptsNoLazyFetch, bool partialClone, bool refused) =>
        Assert.Equal(expected: refused, actual: (WorktreeReportCommand.LazyFetchRefusal(gitAcceptsNoLazyFetch: gitAcceptsNoLazyFetch, partialClone: partialClone) is not null));
    [InlineData("extensions.partialClone", "origin")]
    [InlineData("remote.origin.promisor", "true")]
    [Theory]
    public void APartialCloneIsReportedOnlyWhereThisGitCanForbidLazyFetch(string key, string value) {
        using var checkout = Checkout();

        _ = checkout.Git("config", key, value);
        var result = ConsoleCapture.RunSplit(run: () => WorktreeReportCommand.Execute(repositoryRoot: checkout.Root, into: "main", clock: Clock));

        if (WorktreeReportCommand.GitAcceptsNoLazyFetch) {
            Assert.True(condition: (result.ExitCode == 0), userMessage: result.Error);
        } else {
            Assert.Equal(actual: result.ExitCode, expected: 2);
            Assert.Empty(collection: result.Output);
            Assert.Contains(actualString: result.Error, expectedSubstring: "the repository is a partial clone");
        }
    }
    [InlineData("main^")]
    [InlineData("main~1")]
    [InlineData("main@{0}")]
    [InlineData("main^{commit}")]
    [InlineData("main..main")]
    [InlineData("main:tracked.txt")]
    [InlineData("only-a-tag")]
    [InlineData("origin/only-a-tag")]
    [Theory]
    public void IntoRequiresAnExactBranchName(string into) {
        using var checkout = Checkout();

        _ = CommitChange(checkout: checkout, name: "second.txt", text: "second\n");
        _ = checkout.Git("tag", "refs/heads/only-a-tag");
        _ = checkout.Git("tag", "refs/remotes/origin/only-a-tag");
        var result = ConsoleCapture.RunSplit(run: () => WorktreeReportCommand.Execute(repositoryRoot: checkout.Root, into: into, clock: Clock));

        Assert.Equal(actual: result.ExitCode, expected: 2);
        Assert.Empty(collection: result.Output);
        Assert.Contains(actualString: result.Error, expectedSubstring: "names no local or remote-tracking branch");
    }
    [Fact]
    public void ALocalBranchWinsOverARemoteTrackingBranchWithTheSameName() {
        using var checkout = Checkout();

        _ = checkout.Git("remote", "add", "origin", "https://example.invalid/no-contact.git");
        _ = checkout.Git("update-ref", "refs/remotes/origin/integration", "main");
        _ = checkout.Git("branch", "integration");
        _ = checkout.Git("checkout", "-b", "origin/integration");
        var local = CommitChange(checkout: checkout, name: "local.txt", text: "local\n");

        _ = checkout.Git("branch", "landed-locally", local);
        _ = checkout.Git("checkout", "main");
        using var report = Report(checkout: checkout, into: "origin/integration");

        Assert.Equal(expected: new[] { "integration-branch" }, actual: Blockers(entry: Entry(branch: "origin/integration", report: report)));
        Landed(entry: Entry(branch: "integration", report: report), landed: "ancestor", removable: true);
        Landed(entry: Entry(branch: "landed-locally", report: report), landed: "ancestor", removable: true);
    }
    [InlineData("+refs/heads/*:refs/remotes/team/origin/*", "integration", false)]
    [InlineData("+refs/heads/integration:refs/remotes/team/origin/renamed", "renamed", false)]
    [InlineData("+refs/heads/*:refs/remotes/team/origin/*", "integration", true)]
    [Theory]
    public void RemoteCounterpartUsesTheFetchMapping(string fetch, string remoteBranch, bool symbolic) {
        using var checkout = Checkout();

        _ = checkout.Git("remote", "add", "team/origin", "https://example.invalid/no-contact.git");
        _ = checkout.Git("config", "remote.team/origin.fetch", fetch);
        _ = checkout.Git("update-ref", $"refs/remotes/team/origin/{remoteBranch}", "main");
        _ = checkout.Git("branch", "integration");
        _ = checkout.Git("branch", $"origin/{remoteBranch}");
        if (symbolic) { _ = checkout.Git("symbolic-ref", "refs/remotes/team/origin/HEAD", $"refs/remotes/team/origin/{remoteBranch}"); }
        using var report = Report(checkout: checkout, into: $"team/origin/{(symbolic ? "HEAD" : remoteBranch)}");

        Landed(entry: Entry(branch: "integration", report: report), landed: "ancestor", removable: false);
        Assert.Equal(expected: new[] { "integration-branch" }, actual: Blockers(entry: Entry(branch: "integration", report: report)));
        Landed(entry: Entry(branch: $"origin/{remoteBranch}", report: report), landed: "ancestor", removable: true);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AnUnknownRemoteCounterpartBlocksEveryRemoval(bool ambiguous) {
        using var checkout = Checkout();

        _ = checkout.Git("remote", "add", "origin", "https://example.invalid/no-contact.git");
        _ = checkout.Git("update-ref", "refs/remotes/origin/integration", "main");
        _ = checkout.Git("branch", "integration");
        _ = checkout.Git("branch", "lane");
        if (ambiguous) {
            _ = checkout.Git("config", "--add", "remote.origin.fetch", "+refs/heads/other:refs/remotes/origin/integration");
        } else {
            _ = checkout.Git("config", "--unset-all", "remote.origin.fetch");
        }
        var result = ConsoleCapture.RunSplit(run: () => WorktreeReportCommand.Execute(repositoryRoot: checkout.Root, into: "origin/integration", clock: Clock));

        Assert.Equal(actual: result.ExitCode, expected: 0);
        Assert.Empty(collection: result.Error);
        using var report = JsonDocument.Parse(json: result.Output);

        Assert.Contains(expectedSubstring: "fetch mappings", actualString: Assert.Single(collection: report.RootElement.GetProperty(propertyName: "errors").EnumerateArray()).GetString());
        Assert.All(collection: report.RootElement.GetProperty(propertyName: "entries").EnumerateArray(), action: entry => {
            Assert.False(condition: entry.GetProperty(propertyName: "removable").GetBoolean());
            Assert.Contains(expected: "unreadable", collection: Blockers(entry: entry));
        });
    }
    [Fact]
    public void AMergeWithUnlandedResolutionWorkIsNotPatchEquivalent() {
        using var checkout = Checkout();

        _ = checkout.Git("checkout", "-b", "left");
        var left = CommitChange(checkout: checkout, name: "left.txt", text: "left\n");

        _ = checkout.Git("checkout", "main");
        _ = checkout.Git("checkout", "-b", "right");
        var right = CommitChange(checkout: checkout, name: "right.txt", text: "right\n");

        _ = checkout.Git("checkout", "main");
        _ = checkout.Git("merge", "--ff-only", "left");
        _ = GitAsAuthor(checkout, "merge", "--no-ff", "--message", "land both parents", "right");
        checkout.Write(name: "resolution.txt", text: "work present only in the lane's merge\n");
        _ = checkout.Git("add", "resolution.txt");
        var tree = checkout.Git("write-tree").Trim();
        var merge = GitAsAuthor(checkout, "commit-tree", tree, "-p", left, "-p", right, "-m", "unlanded resolution").Trim();

        _ = checkout.Git("update-ref", "refs/heads/resolution", merge);
        _ = checkout.Git("reset", "--hard", "main");
        using var report = Report(checkout: checkout);

        Landed(entry: Entry(branch: "resolution", report: report), landed: "no", removable: false);
        Assert.Equal(expected: new[] { "unlanded" }, actual: Blockers(entry: Entry(branch: "resolution", report: report)));
    }
    [Fact]
    public void StagedRenameCountsOnceAndGitRefusalStaysVisible() {
        using var checkout = Checkout();

        _ = checkout.Git("branch", "rename");
        var renamed = checkout.AddWorktree(name: "rename", revision: "rename");

        _ = checkout.Git("-C", renamed, "mv", "tracked.txt", "renamed file.txt");
        _ = checkout.Git("branch", "refused");
        var refused = checkout.AddWorktree(name: "refused", revision: "refused");

        File.SetAttributes(path: Path.Combine(path1: refused, path2: ".git"), fileAttributes: FileAttributes.Normal);
        File.WriteAllText(path: Path.Combine(path1: refused, path2: ".git"), contents: "gitdir: nowhere\n");
        using var report = Report(checkout: checkout);
        var rename = Entry(branch: "rename", report: report);

        Assert.Equal(expected: 1, actual: rename.GetProperty(propertyName: "dirty").GetProperty(propertyName: "modified").GetInt32());
        Assert.Equal(expected: 0, actual: rename.GetProperty(propertyName: "dirty").GetProperty(propertyName: "untracked").GetInt32());
        var unreadable = Entry(branch: "refused", report: report);

        Assert.Contains(expectedSubstring: "git rev-parse exited", actualString: unreadable.GetProperty(propertyName: "unreadable").GetString());
        Assert.Equal(expected: new[] { "unreadable" }, actual: Blockers(entry: unreadable));
        Assert.False(condition: unreadable.GetProperty(propertyName: "removable").GetBoolean());
    }
}
