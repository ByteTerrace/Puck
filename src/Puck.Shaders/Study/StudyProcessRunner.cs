using System.Diagnostics;
using System.Text;

namespace Puck.Shaders.Study;

internal sealed class StudyProcessRunner : IStudyProcessRunner {
    public StudyProcessResult Run(string fileName, IReadOnlyList<string> arguments) {
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo {
            CreateNoWindow = true,
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = utf8NoBom,
            StandardOutputEncoding = utf8NoBom,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) { startInfo.ArgumentList.Add(item: argument); }

        using var process = (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: $"Failed to start {fileName}."));
        // Both pipes drain concurrently: a tool that fills one before the other closes would otherwise deadlock a
        // sequential ReadToEnd pair.
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = stderrTask.GetAwaiter().GetResult();

        process.WaitForExit();

        return new StudyProcessResult(ExitCode: process.ExitCode, Stderr: stderr, Stdout: stdout);
    }
}
