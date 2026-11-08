using Puck.Abstractions;

namespace Puck.Cli;

/// <summary>
/// The one way a verb asks git something: every call names the repository it reads through <c>git -C</c>, never
/// the working directory it happens to run in, and runs through <see cref="ChildProcess.RunAsync"/>, so both streams
/// arrive exactly as git wrote them, read as git writes them so no pipe fills. Git never inherits the caller's standard
/// input: it reads the text a call supplies, or nothing, and its input is closed at once, so a caller started with an
/// open pipe on its own input cannot hold git waiting on it. Every call is bounded (<see cref="Bound"/> unless the call
/// names its own), and one that outlives its bound is killed and refused with the git command it ran.
/// </summary>
public static class CliGit {
    private static readonly Lazy<bool> AcceptsNoLazyFetch = new(valueFactory: static () => (Run(repository: ".", "--no-lazy-fetch", "version").ExitCode == 0));
    private static readonly string[] ReadOptions = ["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "maintenance.auto=false", "-c", "gc.auto=0"];

    /// <summary>Whether this Git supports forbidding object downloads during a local report.</summary>
    public static bool GitAcceptsNoLazyFetch => AcceptsNoLazyFetch.Value;

    /// <summary>Refuses a partial-clone report when Git cannot forbid lazy fetches.</summary>
    public static string? LazyFetchRefusal(bool gitAcceptsNoLazyFetch, bool partialClone) => ((gitAcceptsNoLazyFetch || !partialClone)
        ? null : "the repository is a partial clone, and this git (before 2.44) has no --no-lazy-fetch to keep the report from fetching missing objects; run it with git 2.44 or later, or from a full clone.");
    /// <summary>Queries Git without index refreshes, hooks, maintenance or object downloads. Older Git must first
    /// prove the repository has no promisor remote.</summary>
    public static async Task<ChildProcessResult> ReadOnlyAsync(string repository, IEnumerable<string> arguments, CancellationToken cancellationToken = default, string? input = null, TimeSpan? timeout = null) {
        if (!GitAcceptsNoLazyFetch) {
            var configuration = await RunAsync(repository: repository, arguments: [.. ReadOptions, "config", "--null", "--get-regexp", @"^(extensions\.partialclone|remote\..*\.promisor)$"], cancellationToken: cancellationToken, timeout: timeout).ConfigureAwait(continueOnCapturedContext: false);

            if ((configuration.ExitCode is not (0 or 1)) || !string.IsNullOrWhiteSpace(value: configuration.Stderr)) {
                throw new InvalidOperationException(message: $"Cannot establish a local-only Git query: {configuration.Stderr.Trim()}");
            }
            var partialClone = configuration.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0').Any(predicate: static record => {
                var newline = record.IndexOf(value: '\n');
                var key = ((newline < 0) ? record : record[..newline]);
                var value = ((newline < 0) ? string.Empty : record[(newline + 1)..]);

                return (key.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "extensions.partialclone") || !value.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "false"));
            });

            if (LazyFetchRefusal(gitAcceptsNoLazyFetch: false, partialClone: partialClone) is { } refusal) { throw new InvalidOperationException(message: refusal); }
        }
        return await RunAsync(repository: repository, arguments: [.. ReadOptions, .. (GitAcceptsNoLazyFetch ? new[] { "--no-lazy-fetch" } : []), .. arguments], cancellationToken: cancellationToken, input: input, timeout: timeout).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Resolves the absolute common Git directory shared by every checkout.</summary>
    public static async Task<string?> CommonDirectoryAsync(string repository, CancellationToken cancellationToken = default) {
        var result = await RunAsync(repository: repository, arguments: ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var path = result.Stdout.Trim();

        return (((result.ExitCode == 0) && Path.IsPathFullyQualified(path: path)) ? PuckPaths.Normalize(path: path) : null);
    }
    /// <summary>Whether this directory has its own Git entry, including linked worktrees.</summary>
    public static bool HasOwnGitEntry(string repositoryRoot) => Path.Exists(path: Path.Combine(path1: repositoryRoot, path2: ".git"));
    /// <summary>Lists tracked and untracked, unignored paths relative to the queried directory.</summary>
    public static bool TryListFiles(string repository, out string[] paths, out string reason) {
        var result = Run(repository, "ls-files", "-z", "--cached", "--others", "--exclude-standard");

        paths = ((result.ExitCode == 0) ? result.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0') : []);
        reason = ((result.ExitCode == 0) ? string.Empty : result.Stderr.Trim());
        return (result.ExitCode == 0);
    }
    /// <summary>Resolves a full commit SHA, refusing an invalid revision or Git failure.</summary>
    public static async Task<string> CommitAsync(string repository, string revision = "HEAD") {
        var result = await CaptureAsync(repository: repository, arguments: ["rev-parse", "--verify", "--end-of-options", (revision + "^{commit}")]).ConfigureAwait(continueOnCapturedContext: false);
        var commit = result.Trim();

        ValidateCommit(commit: commit);
        return commit;
    }
    /// <summary>Requires the full lowercase SHA used in release records.</summary>
    public static void ValidateCommit(string commit) {
        if ((commit.Length != 40) || !commit.All(predicate: static c => (c is >= '0' and <= '9' or >= 'a' and <= 'f'))) {
            throw new ArgumentException(message: "Expected a full lowercase Git commit SHA.");
        }
    }

    /// <summary>How long a git call may run when the call names no bound of its own.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromMinutes(minutes: 10);

    /// <summary>Runs git against <paramref name="repository"/> and returns its exit code and both streams raw.</summary>
    /// <param name="repository">The directory git runs against, passed as <c>-C</c>.</param>
    /// <param name="arguments">The git arguments that follow <c>-C</c>.</param>
    /// <param name="cancellationToken">Cancels the run and kills git.</param>
    /// <param name="input">Text to feed to git; with none, git reads an empty, closed standard input.</param>
    /// <param name="timeout">How long git may run; <see cref="Bound"/> when <see langword="null"/>.</param>
    /// <returns>The exit code and both captured streams; a nonzero exit is an answer, not a failure.</returns>
    /// <exception cref="TimeoutException">Git outlived its bound and was killed; the message names the command.</exception>
    public static async Task<ChildProcessResult> RunAsync(string repository, IEnumerable<string> arguments, CancellationToken cancellationToken = default, string? input = null, TimeSpan? timeout = null) {
        string[] command = ["-C", repository, .. arguments];
        var bound = (timeout ?? Bound);
        var run = await ChildProcess.RunAsync(
            arguments: command,
            cancellationToken: cancellationToken,
            fileName: "git",
            input: (input ?? string.Empty),
            timeout: bound
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (run.TimedOut
            ? throw new TimeoutException(message: $"git did not exit within {bound} and was killed: git {string.Join(separator: ' ', values: command)}")
            : run);
    }
    /// <summary>Runs git against <paramref name="repository"/>, waiting synchronously, and returns its exit code and
    /// both streams raw.</summary>
    /// <param name="repository">The directory git runs against, passed as <c>-C</c>.</param>
    /// <param name="arguments">The git arguments that follow <c>-C</c>.</param>
    /// <returns>The exit code and both captured streams.</returns>
    /// <exception cref="TimeoutException">Git outlived <see cref="Bound"/> and was killed.</exception>
    public static ChildProcessResult Run(string repository, params string[] arguments) =>
        RunAsync(
            arguments: arguments,
            repository: repository
        ).GetAwaiter().GetResult();
    /// <summary>Runs git against <paramref name="repository"/> and returns its standard output, requiring exit code
    /// zero.</summary>
    /// <param name="repository">The directory git runs against, passed as <c>-C</c>.</param>
    /// <param name="arguments">The git arguments that follow <c>-C</c>.</param>
    /// <param name="cancellationToken">Cancels the run and kills git.</param>
    /// <returns>Git's standard output, untrimmed.</returns>
    /// <exception cref="InvalidOperationException">Git exited nonzero; the message carries its standard error.</exception>
    /// <exception cref="TimeoutException">Git outlived <see cref="Bound"/> and was killed.</exception>
    public static async Task<string> CaptureAsync(string repository, IEnumerable<string> arguments, CancellationToken cancellationToken = default) {
        var run = await RunAsync(
            arguments: arguments,
            cancellationToken: cancellationToken,
            repository: repository
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (run.ExitCode != 0) {
            throw new InvalidOperationException(message: $"git exited with code {run.ExitCode}. {run.Stderr}".TrimEnd());
        }
        if (!string.IsNullOrWhiteSpace(value: run.Stderr)) { Console.Error.WriteLine(value: run.Stderr); }

        return run.Stdout;
    }
    /// <summary>Indicates whether <paramref name="candidate"/> is an ancestor of <paramref name="descendant"/> in
    /// <paramref name="repository"/>.</summary>
    /// <param name="repository">The directory git runs against.</param>
    /// <param name="candidate">The possible ancestor.</param>
    /// <param name="descendant">The commit to test against.</param>
    /// <returns><see langword="true"/> when git answers yes; <see langword="false"/> for no or for an unknown
    /// revision.</returns>
    public static bool IsAncestor(string repository, string candidate, string descendant) =>
        (Run(
            arguments: ["merge-base", "--is-ancestor", candidate, descendant],
            repository: repository
        ).ExitCode == 0);
    /// <summary>Resolves the best common ancestor of two revisions in <paramref name="repository"/>.</summary>
    /// <param name="repository">The directory git runs against.</param>
    /// <param name="first">One revision, such as <c>HEAD</c>.</param>
    /// <param name="second">The other revision, such as the branch a change lands on.</param>
    /// <param name="mergeBase">The merge base's full object name, or empty when the revisions share none or either does
    /// not resolve.</param>
    /// <returns><see langword="true"/> when git names a merge base.</returns>
    public static bool TryMergeBase(string repository, string first, string second, out string mergeBase) {
        var result = Run(
            arguments: ["merge-base", first, second],
            repository: repository
        );

        mergeBase = ((result.ExitCode == 0)
            ? result.Stdout.Trim()
            : string.Empty);

        return (mergeBase.Length != 0);
    }
    /// <summary>Resolves a revision to the full name of the commit it names in <paramref name="repository"/>.</summary>
    /// <param name="repository">The directory git runs against.</param>
    /// <param name="revision">The revision to resolve.</param>
    /// <param name="resolved">The full object name, or empty when the revision names no commit.</param>
    /// <returns><see langword="true"/> when the revision names a commit the repository carries.</returns>
    public static bool TryResolveCommit(string repository, string revision, out string resolved) {
        resolved = Run(
            arguments: ["rev-parse", "--verify", "--quiet", $"{revision}^{{commit}}"],
            repository: repository
        ).Stdout.Trim();

        return (resolved.Length != 0);
    }
    /// <summary>Resolves the main checkout of the repository <paramref name="repository"/> belongs to: the directory that
    /// holds the common git directory, the same answer from every worktree. Shared per-repository state (leases, the
    /// worktrees folder) lives under it, never under a linked worktree.</summary>
    /// <param name="repository">Any worktree of the repository.</param>
    /// <param name="mainCheckout">The main checkout's absolute path, or empty when git cannot name it.</param>
    /// <returns><see langword="true"/> when resolved.</returns>
    public static bool TryResolveMainCheckout(string repository, out string mainCheckout) {
        var common = CommonDirectoryAsync(repository: repository).GetAwaiter().GetResult();

        mainCheckout = ((common is not null)
            ? (Path.GetDirectoryName(path: Path.TrimEndingDirectorySeparator(path: common)) ?? string.Empty)
            : string.Empty);
        return (mainCheckout.Length != 0);
    }
    /// <summary>Resolves the integration branch: the branch the main checkout holds, which by the repository's
    /// convention tracks the integration branch while lanes work in linked worktrees.</summary>
    /// <param name="repository">Any worktree of the repository.</param>
    /// <param name="branch">The branch's short name, or empty when the main checkout cannot be named or is detached.</param>
    /// <returns><see langword="true"/> when resolved.</returns>
    public static bool TryResolveIntegrationBranch(string repository, out string branch) {
        branch = (TryResolveMainCheckout(mainCheckout: out var mainCheckout, repository: repository)
            ? Run(mainCheckout, "symbolic-ref", "--short", "-q", "HEAD").Stdout.Trim()
            : string.Empty);
        return (branch.Length != 0);
    }
}
