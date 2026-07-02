using System.Diagnostics;
using System.Text;

namespace Puck.Post;

/// <summary>Runs the POST's own executable as a Tier-D probe child (<c>--probe &lt;name&gt;</c>), captures its output,
/// and enforces a hang timeout — the shared plumbing for <see cref="DeviceLostStage"/> and
/// <see cref="HotSwitchStage"/>. The probes run in their own process because they destabilize live subsystems (a
/// deliberate device loss; a presenter swap) that the battery's host must not share, per the plan's Tier-D isolation
/// decision.</summary>
internal static class PostProbeProcess {
    /// <summary>The probe hang budget: generous against a slow first-time shader/pipeline warm-up, small against a
    /// wedged recovery.</summary>
    public const int TimeoutSeconds = 60;

    /// <summary>A finished probe run.</summary>
    /// <param name="ExitCode">The child's exit code (-1 when it timed out and was killed).</param>
    /// <param name="Output">The child's combined stdout + stderr.</param>
    /// <param name="TimedOut">Whether the child overran <see cref="TimeoutSeconds"/> and was killed.</param>
    public readonly record struct ProbeResult(int ExitCode, string Output, bool TimedOut) {
        /// <summary>The probe's own "ok" summary line, or the output tail when none was printed.</summary>
        public string OkLine {
            get {
                foreach (var line in Output.Split(separator: '\n')) {
                    if (line.Contains(value: "PROBE", comparisonType: StringComparison.Ordinal) && line.Contains(value: "ok |", comparisonType: StringComparison.Ordinal)) {
                        return line.Trim();
                    }
                }

                return OutputTail;
            }
        }
        /// <summary>The last few output lines, flattened for a one-line stage detail.</summary>
        public string OutputTail {
            get {
                var lines = Output.Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                return string.Join(separator: " | ", values: lines.TakeLast(count: 3));
            }
        }
    }

    /// <summary>Launches the probe child and waits for it.</summary>
    /// <param name="probe">The probe name (a <see cref="PostProbeNode"/> mode).</param>
    /// <param name="environment">Extra environment variables for the child (e.g. the synthetic-loss injection), or <see langword="null"/>.</param>
    /// <returns>The finished run.</returns>
    /// <exception cref="InvalidOperationException">The current executable path could not be determined.</exception>
    public static ProbeResult Run(string probe, IReadOnlyDictionary<string, string>? environment) {
        var executable = (Environment.ProcessPath ?? throw new InvalidOperationException(message: "Environment.ProcessPath is unavailable; the Tier-D probes cannot relaunch the POST."));
        var startInfo = new ProcessStartInfo {
            CreateNoWindow = true,
            FileName = executable,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(item: "--probe");
        startInfo.ArgumentList.Add(item: probe);

        if (environment is not null) {
            foreach (var (name, value) in environment) {
                startInfo.Environment[name] = value;
            }
        }

        using var process = (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: $"The probe child '{probe}' failed to start."));

        var output = new StringBuilder();

        process.OutputDataReceived += (_, arguments) => { if (arguments.Data is not null) { lock (output) { _ = output.AppendLine(value: arguments.Data); } } };
        process.ErrorDataReceived += (_, arguments) => { if (arguments.Data is not null) { lock (output) { _ = output.AppendLine(value: arguments.Data); } } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(milliseconds: (TimeoutSeconds * 1000))) {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();

            lock (output) {
                return new ProbeResult(ExitCode: -1, Output: output.ToString(), TimedOut: true);
            }
        }

        // Flush the async readers before sampling the buffer.
        process.WaitForExit();

        lock (output) {
            return new ProbeResult(ExitCode: process.ExitCode, Output: output.ToString(), TimedOut: false);
        }
    }
}
