namespace Puck.Cli.WorktreeBase;

/// <summary>Captured-output git invocation against an explicit working directory, via <c>git -C &lt;path&gt;</c>
/// rather than the caller's own current directory — the verb this backs resolves a worktree path a caller
/// supplies, not the CLI's own cwd.</summary>
internal static class WorktreeBaseGit {
    public static WorktreeBaseGitResult Run(string path, params string[] arguments) {
        try {
            var result = CliProcess.RunCapturedRaw(fileName: "git", arguments: ["-C", path, .. arguments]);

            return new WorktreeBaseGitResult(ExitCode: result.ExitCode, Stderr: result.Stderr.Trim(), Stdout: result.Stdout.Trim());
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            return new WorktreeBaseGitResult(ExitCode: -1, Stderr: exception.Message, Stdout: string.Empty);
        }
    }
}
/// <param name="ExitCode">The child process's exit code, or <c>-1</c> when git itself could not be started.</param>
/// <param name="Stderr">The captured, trimmed standard error text.</param>
/// <param name="Stdout">The captured, trimmed standard output text.</param>
internal readonly record struct WorktreeBaseGitResult(int ExitCode, string Stderr, string Stdout);
