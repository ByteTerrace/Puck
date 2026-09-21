using Xunit;

namespace Puck.Cli.Tests;

public sealed class CliProcessHandshakeTests {
    private static (string Executable, string[] Arguments) Reader() => (OperatingSystem.IsWindows()
        ? ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
            "[Console]::Error.WriteLine([Console]::ReadLine()); [Console]::WriteLine([Console]::ReadLine())"])
        : ("/bin/sh", ["-c", "read first; printf '%s\\n' \"$first\" >&2; read last; printf '%s\\n' \"$last\""]));

    [Fact]
    public void OutputReleasesContinuationWithoutClosingInputEarly() {
        var (executable, arguments) = Reader();
        var result = CliProcess.RunCaptured(executable, arguments, "ready\n", TimeSpan.FromSeconds(seconds: 20),
            continueWhen: line => ((line.Stream == CliProcessOutputStream.Stderr) && (line.Line == "ready")),
            continuationInput: "finished\n");

        Assert.False(condition: result.TimedOut);
        Assert.Equal(actual: result.ExitCode, expected: 0);
        Assert.Contains(result.OutputLines, line => ((line.Stream == CliProcessOutputStream.Stdout) && (line.Line == "finished")));
    }
    [Fact]
    public void EarlyExitDoesNotWaitForAnAbsentResponse() {
        var result = CliProcess.RunCaptured("dotnet", ["--version"], "", TimeSpan.FromSeconds(seconds: 20),
            continueWhen: _ => false, continuationInput: "unused\n");

        Assert.False(condition: result.TimedOut);
        Assert.Equal(actual: result.ExitCode, expected: 0);
    }
    [Fact]
    public void TimeoutStillTerminatesAChildWaitingForContinuation() {
        var (executable, arguments) = Reader();
        var result = CliProcess.RunCaptured(executable, arguments, "ready\n", TimeSpan.FromSeconds(seconds: 3),
            continueWhen: _ => false, continuationInput: "unused\n");

        Assert.True(condition: result.TimedOut);
        Assert.Contains(result.OutputLines, line => ((line.Stream == CliProcessOutputStream.Stderr) && (line.Line == "ready")));
    }
}
