using Puck.Testing;
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
        var result = CliProcess.RunCaptured(executable, arguments, "ready\n", Timeout.InfiniteTimeSpan,
            continueWhen: line => ((line.Stream == CliProcessOutputStream.Stderr) && (line.Line == "ready")),
            continuationInput: "finished\n", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(actual: result.ExitCode, expected: 0);
        Assert.Contains(collection: result.OutputLines, filter: line => ((line.Stream == CliProcessOutputStream.Stdout) && (line.Line == "finished")));
    }
    [Fact]
    public void EarlyExitDoesNotWaitForAnAbsentResponse() {
        var result = CliProcess.RunCaptured("dotnet", ["--version"], "", Timeout.InfiniteTimeSpan,
            continueWhen: _ => false, continuationInput: "unused\n", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(actual: result.ExitCode, expected: 0);
    }
    // Cancelled once the child has echoed its first line and is blocked reading a line that never comes. The call can
    // only return after the child has exited, so returning at all proves the kill.
    [Fact]
    public async Task CancellationKillsAChildWaitingForContinuationAndThrows() {
        var (executable, arguments) = Reader();
        using var cancellation = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Task.Run(function: () => CliProcess.RunCaptured(executable, arguments, "ready\n", Timeout.InfiniteTimeSpan,
            continueWhen: line => {
                if ((line.Stream == CliProcessOutputStream.Stderr) && (line.Line == "ready")) { waiting.TrySetResult(); }
                return false;
            }, continuationInput: "unused\n", cancellationToken: cancellation.Token));

        await waiting.Task.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => run.WaitAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
    [Fact]
    public void AnAlreadyCancelledTokenStartsNoChild() {
        using var cancellation = new CancellationTokenSource();

        cancellation.Cancel();

        _ = Assert.ThrowsAny<OperationCanceledException>(testCode: () => CliProcess.RunCaptured("dotnet", ["--version"], "", TimeSpan.FromSeconds(seconds: 20),
            cancellationToken: cancellation.Token));
    }
    // The timeout fires on a test clock once the child has echoed its first line and is blocked on the continuation,
    // so the kill always lands on a child that is provably waiting, however slowly the child started.
    [Fact]
    public async Task TimeoutStillTerminatesAChildWaitingForContinuation() {
        var (executable, arguments) = Reader();
        var clock = new VirtualClock();
        var waiting = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Task.Run(function: () => CliProcess.RunCaptured(executable, arguments, "ready\n", TimeSpan.FromSeconds(seconds: 3),
            continueWhen: line => {
                if ((line.Stream == CliProcessOutputStream.Stderr) && (line.Line == "ready")) { waiting.TrySetResult(); }
                return false;
            }, continuationInput: "unused\n", clock: clock));

        await waiting.Task.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        await clock.WhenArmedAsync(
            count: 1,
            ct: TestContext.Current.CancellationToken,
            dueTime: TimeSpan.FromSeconds(seconds: 3)
        );
        clock.Advance(by: TimeSpan.FromSeconds(seconds: 3));
        var result = await run;

        Assert.True(condition: result.TimedOut);
        Assert.NotEqual(expected: 0, actual: result.ExitCode);
        Assert.Contains(collection: result.OutputLines, filter: line => ((line.Stream == CliProcessOutputStream.Stderr) && (line.Line == "ready")));
    }
}
