using System.Diagnostics;
using System.Text;

namespace Puck.Cli.Landing;

/// <summary>Captured-output git invocation. <see cref="CliProcess"/> streams a child's output through to the
/// console, which is right for a verb that shells out to another tool; this verb needs to READ what git says.</summary>
internal static class Git {
    public static string Capture(params string[] arguments) {
        var startInfo = new ProcessStartInfo {
            FileName = "git",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(item: argument);
        }

        using var process = (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: "Failed to start git."));
        var output = process.StandardOutput.ReadToEnd();

        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return output;
    }
    /// <summary>Indicates whether <paramref name="candidate"/> is an ancestor of <paramref name="descendant"/>.</summary>
    /// <param name="candidate">The possible ancestor.</param>
    /// <param name="descendant">The commit to test against.</param>
    public static bool IsAncestor(string candidate, string descendant) {
        // --is-ancestor answers through the exit code and prints nothing, so the captured stream is empty either way.
        var startInfo = new ProcessStartInfo {
            FileName = "git",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(item: "merge-base");
        startInfo.ArgumentList.Add(item: "--is-ancestor");
        startInfo.ArgumentList.Add(item: candidate);
        startInfo.ArgumentList.Add(item: descendant);

        using var process = (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: "Failed to start git."));

        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode == 0);
    }
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
