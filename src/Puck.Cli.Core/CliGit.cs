using Puck.Hosting;

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
}
