using Puck.Testing;

using Xunit;

namespace Puck.Cli.Core.Tests;

/// <summary>A verb's deadline runs on the clock its composition root hands it: work that never finishes ends exactly
/// when the deadline expires on a <see cref="VirtualClock"/>, not before.</summary>
public sealed class CliDeadlineLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A checked tool run that never exits is killed when its timeout expires on the clock and throws
    /// <see cref="TimeoutException"/> rather than reading as an exit code.</summary>
    [Fact]
    public async Task CheckedRun_ThrowsTimeoutWhenItsTimeoutExpiresOnTheClock() {
        var clock = new VirtualClock();
        var timeout = TimeSpan.FromSeconds(seconds: 3);

        var (executable, arguments) = (OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 600" })
            : ("/bin/sh", new[] { "-c", "sleep 600" }));
        var run = CliProcess.RunCheckedAsync(
            arguments: arguments,
            cancellationToken: Token,
            capture: true,
            clock: clock,
            fileName: executable,
            workingDirectory: Environment.CurrentDirectory,
            timeout: timeout
        );

        await clock.ExpireAsync(
            ct: Token,
            dueTime: timeout,
            pending: run
        );

        _ = await Assert.ThrowsAsync<TimeoutException>(testCode: () => run);
    }
}
