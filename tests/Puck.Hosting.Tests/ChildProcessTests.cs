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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_EndsAtTheDrainGraceOrCancellationWhenAProcessTheChildStartedHoldsItsStreams(bool cancel) {
        var clock = new VirtualClock();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        // The red leg never returns stdout. Keep the inheritor's PID outside the pipe so cleanup still kills it.
        var pidFile = Path.GetTempFileName();
        var quotedPidFile = pidFile.Replace(oldValue: "'", newValue: (OperatingSystem.IsWindows() ? "''" : "'\"'\"'"), comparisonType: StringComparison.Ordinal);

        var (executable, arguments) = (OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", $"$held = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds {InheritorSeconds}' -NoNewWindow -PassThru; [IO.File]::WriteAllText('{quotedPidFile}', [string]$held.Id); [Console]::Out.Write($held.Id); [Console]::Error.Write('exited')" })
            : ("/bin/sh", new[] { "-c", $"sleep {InheritorSeconds} & held=$!; printf '%s' $held > '{quotedPidFile}'; printf '%s' $held; printf exited 1>&2" }));
        var run = ChildProcess.RunAsync(
            arguments: arguments,
            cancellationToken: cancellation.Token,
            clock: clock,
            fileName: executable
        );

        try {
            if (cancel) {
                await clock.WhenArmedAsync(count: 1, ct: Token, dueTime: ChildProcess.ExitDrainGrace).WaitAsync(timeout: HangGuard, cancellationToken: Token);
                await cancellation.CancelAsync();
                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => run.WaitAsync(timeout: HangGuard, cancellationToken: Token));
                Assert.Equal(expected: TimeSpan.Zero, actual: clock.Elapsed);
                return;
            }
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
                s: File.ReadAllText(path: pidFile),
                style: NumberStyles.None
            )) {
                try {
                    using var inheritor = Process.GetProcessById(processId: held);

                    inheritor.Kill();
                    await inheritor.WaitForExitAsync(cancellationToken: Token);
                } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException)) {
                    // It has already exited.
                }
            }
            File.Delete(path: pidFile);
            await cancellation.CancelAsync();
            try { await run.WaitAsync(timeout: HangGuard, cancellationToken: Token); } catch (OperationCanceledException) { }
        }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task RunAsync_TimeoutOrCancellationKillsAChildWithABlockedInputWrite(bool cancel) {
        var clock = new VirtualClock();
        var timeout = TimeSpan.FromSeconds(seconds: 3);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token: Token);

        var (executable, arguments) = (OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 180" })
            : ("/bin/sh", new[] { "-c", "sleep 180" }));
        var run = ChildProcess.RunAsync(fileName: executable, arguments: arguments, input: new string(c: 'i', count: StreamLength),
            clock: clock, timeout: timeout, cancellationToken: cancellation.Token);

        try {
            if (cancel) {
                await cancellation.CancelAsync();
                _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => run.WaitAsync(timeout: HangGuard, cancellationToken: Token));
            } else {
                await clock.ExpireAsync(dueTime: timeout, pending: run, ct: Token).WaitAsync(timeout: HangGuard, cancellationToken: Token);
                var result = await run.WaitAsync(timeout: HangGuard, cancellationToken: Token);

                Assert.True(condition: result.TimedOut);
                Assert.NotEqual(expected: 0, actual: result.ExitCode);
            }
        } finally {
            await cancellation.CancelAsync();
            try { await run.WaitAsync(timeout: HangGuard, cancellationToken: Token); } catch (OperationCanceledException) { }
        }
    }
}
