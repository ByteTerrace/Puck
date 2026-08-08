using System.Diagnostics;

namespace Puck.Cli;

// Child-process launch for the verbs that shell out to another tool. The child inherits this console,
// so its output streams live instead of being captured and replayed.
internal static class CliProcess {
    public static int RunStreamed(string fileName, params string[] arguments) {
        var startInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };

        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(item: argument);
        }

        using var process = (Process.Start(startInfo: startInfo)
            ?? throw new InvalidOperationException(message: $"Failed to start {fileName}."));

        process.WaitForExit();

        return process.ExitCode;
    }
}
