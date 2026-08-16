using System.Diagnostics;
using System.Text;

namespace Puck.Cli.WorktreeBase;

/// <summary>Captured-output git invocation against an explicit working directory, via <c>git -C &lt;path&gt;</c>
/// rather than the caller's own current directory — the verb this backs resolves a worktree path a caller
/// supplies, not the CLI's own cwd.</summary>
internal static class WorktreeBaseGit {
    public static WorktreeBaseGitResult Run(string path, params string[] arguments) {
        var startInfo = new ProcessStartInfo {
            CreateNoWindow = true,
            FileName = "git",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(item: "-C");
        startInfo.ArgumentList.Add(item: path);

        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(item: argument);
        }

        Process process;

        try {
            process = (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: "Failed to start git."));
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            return new WorktreeBaseGitResult(ExitCode: -1, Stderr: exception.Message, Stdout: string.Empty);
        }

        using var _ = process;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        process.WaitForExit();

        return new WorktreeBaseGitResult(ExitCode: process.ExitCode, Stderr: stderr.Trim(), Stdout: stdout.Trim());
    }
}
/// <param name="ExitCode">The child process's exit code, or <c>-1</c> when git itself could not be started.</param>
/// <param name="Stderr">The captured, trimmed standard error text.</param>
/// <param name="Stdout">The captured, trimmed standard output text.</param>
internal readonly record struct WorktreeBaseGitResult(int ExitCode, string Stderr, string Stdout);
