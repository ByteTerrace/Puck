using System.Globalization;
using Puck.Hosting;

namespace Puck.Cli.Bench;

/// <summary>Finds an offline analysis tool (the requested path, then PATH, then the platform's installation) and runs
/// it.</summary>
internal static class ReferenceTools {
    /// <summary>Returns the executable to run, or null when none of the documented locations carries it.</summary>
    /// <param name="name">The tool's name without an extension.</param>
    /// <param name="requested">The explicitly requested path, when one was given.</param>
    public static string? Find(string name, string? requested) {
        if (!string.IsNullOrWhiteSpace(value: requested)) { return Path.GetFullPath(path: requested); }

        var executable = (OperatingSystem.IsWindows()
            ? (name + ".exe")
            : name);

        foreach (var directory in (Environment.GetEnvironmentVariable(variable: "PATH") ?? string.Empty).Split(
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
            separator: Path.PathSeparator
        )) {
            var candidate = Path.Combine(
                path1: directory,
                path2: executable
            );

            if (File.Exists(path: candidate)) { return candidate; }
        }
        if (OperatingSystem.IsWindows()) {
            var installed = Path.Combine(
                path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.ProgramFiles),
                path2: "LLVM/bin",
                path3: executable
            );

            if (File.Exists(path: installed)) { return installed; }
        }
        return null;
    }
    /// <summary>Runs one tool through the CLI's one tool runner and returns whether it exited zero, together with what
    /// it wrote. A tool that cannot start, times out, or is cancelled answers a failure naming why, never an exception,
    /// because a missing or refusing analysis tool is evidence the caller reports.</summary>
    /// <param name="executable">The executable to run.</param>
    /// <param name="arguments">Its arguments, passed one element per argument.</param>
    /// <param name="standardInput">What to write to its standard input, or null to leave it closed.</param>
    /// <param name="timeout">How long, on <paramref name="clock"/>, the tool may run before it is killed.</param>
    /// <param name="clock">The clock the timeout runs on.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public static async Task<(bool Success, string Output, string Error)> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan timeout,
        TimeProvider clock,
        CancellationToken cancellationToken
    ) {
        ChildProcessResult run;

        try {
            run = await CliProcess.RunAsync(
                arguments: arguments,
                cancellationToken: cancellationToken,
                clock: clock,
                fileName: executable,
                input: standardInput,
                timeout: timeout
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            return (false, string.Empty, "cancelled");
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            return (false, string.Empty, exception.Message);
        }
        return (run.TimedOut
            ? (false, string.Empty, $"timed out after {timeout.TotalSeconds.ToString(provider: CultureInfo.InvariantCulture)} seconds")
            : ((run.ExitCode == 0), run.Stdout, run.Stderr));
    }
}
