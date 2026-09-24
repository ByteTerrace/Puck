using Puck.Hosting;

namespace Puck.Cli;

/// <summary>
/// The one way a verb asks git something: every call names the repository it reads through <c>git -C</c>, never
/// the working directory it happens to run in, and runs through <see cref="ChildProcess.RunAsync"/>, so both streams
/// arrive exactly as git wrote them.
/// </summary>
internal static class CliGit {
    /// <summary>Runs git against <paramref name="repository"/> and returns its exit code and both streams raw.</summary>
    /// <param name="repository">The directory git runs against, passed as <c>-C</c>.</param>
    /// <param name="arguments">The git arguments that follow <c>-C</c>.</param>
    /// <param name="cancellationToken">Cancels the run and kills git.</param>
    /// <returns>The exit code and both captured streams; a nonzero exit is an answer, not a failure.</returns>
    public static Task<ChildProcessResult> RunAsync(string repository, IEnumerable<string> arguments, CancellationToken cancellationToken = default) =>
        ChildProcess.RunAsync(
            arguments: ["-C", repository, .. arguments],
            cancellationToken: cancellationToken,
            fileName: "git"
        );
    /// <summary>Runs git against <paramref name="repository"/>, waiting synchronously, and returns its exit code and
    /// both streams raw.</summary>
    /// <param name="repository">The directory git runs against, passed as <c>-C</c>.</param>
    /// <param name="arguments">The git arguments that follow <c>-C</c>.</param>
    /// <returns>The exit code and both captured streams.</returns>
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
    public static Task<string> CaptureAsync(string repository, IEnumerable<string> arguments, CancellationToken cancellationToken = default) =>
        CliProcess.RunCheckedAsync(
            arguments: ["-C", repository, .. arguments],
            cancellationToken: cancellationToken,
            capture: true,
            fileName: "git"
        );
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
