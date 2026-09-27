using System.Diagnostics;
using System.Globalization;

using Puck.Testing;

namespace Puck.Hosting.Tests;

public sealed class ChildProcessTests {
    // Outlives HangGuard, so a runner that waits for the inheriting process to close the pipes is still waiting when
    // the guard fails the law.
    private const int InheritorSeconds = 180;
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
    /// <summary>A child that exits while a process it started still holds both inherited output streams ends the run
    /// exactly when <see cref="ChildProcess.ExitDrainGrace"/> elapses on the run's clock, with everything the child
    /// wrote, rather than when that process exits. This is the shape of a build tool whose node or compiler server
    /// outlives it.</summary>
    [Fact]
    public async Task RunAsync_EndsAtTheDrainGraceWhenAProcessTheChildStartedHoldsItsStreams() {
        var clock = new VirtualClock();

        var (executable, arguments) = (OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", $"$held = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds {InheritorSeconds}' -NoNewWindow -PassThru; [Console]::Out.Write($held.Id); [Console]::Error.Write('exited')" })
            : ("/bin/sh", new[] { "-c", $"sleep {InheritorSeconds} & printf '%s' $!; printf exited 1>&2" }));
        var run = ChildProcess.RunAsync(
            arguments: arguments,
            cancellationToken: Token,
            clock: clock,
            fileName: executable
        );
        var inheritorId = "";

        try {
            await clock.ExpireAsync(
                ct: Token,
                dueTime: ChildProcess.ExitDrainGrace,
                pending: run
            ).WaitAsync(
                cancellationToken: Token,
                timeout: HangGuard
            );

            var result = await run.WaitAsync(
                cancellationToken: Token,
                timeout: HangGuard
            );

            inheritorId = result.Stdout;
            Assert.False(condition: result.TimedOut);
            Assert.Equal(
                actual: result.ExitCode,
                expected: 0
            );
            Assert.Equal(
                actual: result.Stderr,
                expected: "exited"
            );
            Assert.True(condition: int.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out _,
                s: result.Stdout,
                style: NumberStyles.None
            ), userMessage: $"the child's own output arrives whole: '{result.Stdout}'");
        } finally {
            if (int.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out var held,
                s: inheritorId,
                style: NumberStyles.None
            )) {
                try {
                    using var inheritor = Process.GetProcessById(processId: held);

                    inheritor.Kill();
                } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
                    // It has already exited.
                }
            }
        }
    }
}
