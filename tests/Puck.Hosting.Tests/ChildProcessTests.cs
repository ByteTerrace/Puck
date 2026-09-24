namespace Puck.Hosting.Tests;

public sealed class ChildProcessTests {
    // Several times the largest default pipe buffer (64 KiB), so a runner that waits for exit before draining a stream
    // leaves the child blocked on a full pipe.
    private const int StreamLength = (256 * 1024);

    // Bounds a run the law expects to finish on its own. A runner that deadlocks reads as TimedOut, and one that blocks
    // its caller before returning a task fails the wait, so neither hangs the suite.
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(minutes: 2);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A child that writes more than a pipe buffer to both standard output and standard error runs to exit,
    /// and both streams arrive whole and unmerged.</summary>
    [Fact]
    public async Task RunAsync_DrainsBothStreamsPastAPipeBuffer() {
        var (executable, arguments) = (OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", $"[Console]::Out.Write('o' * {StreamLength}); [Console]::Error.Write('e' * {StreamLength})" })
            : ("/bin/sh", new[] { "-c", $"head -c {StreamLength} /dev/zero | tr '\\0' o; head -c {StreamLength} /dev/zero | tr '\\0' e 1>&2" }));
        var run = await Task.Run(
            cancellationToken: Token,
            function: () => ChildProcess.RunAsync(
                arguments: arguments,
                cancellationToken: Token,
                fileName: executable,
                timeout: HangGuard
            )
        ).WaitAsync(
            cancellationToken: Token,
            timeout: (HangGuard + HangGuard)
        );

        Assert.False(condition: run.TimedOut, userMessage: "the run did not finish, so a stream was left undrained");
        Assert.Equal(
            actual: run.ExitCode,
            expected: 0
        );
        Assert.Equal(
            actual: run.Stdout,
            expected: new string(
                c: 'o',
                count: StreamLength
            )
        );
        Assert.Equal(
            actual: run.Stderr,
            expected: new string(
                c: 'e',
                count: StreamLength
            )
        );
    }
}
