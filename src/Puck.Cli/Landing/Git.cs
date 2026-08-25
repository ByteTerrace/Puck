namespace Puck.Cli.Landing;

/// <summary>Captured-output git invocation, over <see cref="CliProcess.RunCapturedRaw"/>. <see cref="CliProcess.RunStreamed"/>
/// and <see cref="CliProcess.RunCaptured"/> stream or reconstruct a child's output line-by-line, which is right
/// for a verb that shells out to another tool; this verb needs to READ exactly what git said.</summary>
internal static class Git {
    public static string Capture(params string[] arguments) =>
        CliProcess.RunCapturedRaw(fileName: "git", arguments: arguments).Stdout;
    /// <summary>Indicates whether <paramref name="candidate"/> is an ancestor of <paramref name="descendant"/>.</summary>
    /// <param name="candidate">The possible ancestor.</param>
    /// <param name="descendant">The commit to test against.</param>
    public static bool IsAncestor(string candidate, string descendant) =>
        // --is-ancestor answers through the exit code and prints nothing, so the captured stream is empty either way.
        (CliProcess.RunCapturedRaw(fileName: "git", arguments: ["merge-base", "--is-ancestor", candidate, descendant]).ExitCode == 0);
    /// <summary>Resolves a revision to its full object name, refusing an unknown one by name rather than letting a
    /// typo'd ref resolve to nothing and read as a clean landing.</summary>
    /// <param name="revision">The revision to resolve.</param>
    /// <param name="resolved">The full object name, on success.</param>
    /// <param name="error">Why the revision could not be resolved.</param>
    public static bool TryResolve(string revision, out string resolved, out string error) {
        resolved = Capture("rev-parse", "--verify", "--quiet", $"{revision}^{{commit}}").Trim();

        if (resolved.Length == 0) {
            error = "unknown revision (not a commit this repository carries).";

            return false;
        }

        error = string.Empty;

        return true;
    }
}
