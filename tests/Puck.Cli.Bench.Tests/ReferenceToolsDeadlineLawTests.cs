using Puck.Testing;

using Xunit;

namespace Puck.Cli.Bench.Tests;

/// <summary>A verb's deadline runs on the clock its composition root hands it: work that never finishes ends exactly
/// when the deadline expires on a <see cref="VirtualClock"/>, not before.</summary>
public sealed class ReferenceToolsDeadlineLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A reference tool that never exits is killed when its timeout expires on the clock, and the run names
    /// the timeout.</summary>
    [Fact]
    public async Task ToolRun_IsKilledWhenItsTimeoutExpiresOnTheClock() {
        var clock = new VirtualClock();
        var timeout = TimeSpan.FromSeconds(seconds: 3);

        var (executable, arguments) = (OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 600" })
            : ("/bin/sh", new[] { "-c", "sleep 600" }));
        var run = ReferenceTools.RunAsync(
            arguments: arguments,
            cancellationToken: Token,
            clock: clock,
            executable: executable,
            standardInput: null,
            timeout: timeout
        );

        await clock.ExpireAsync(
            ct: Token,
            dueTime: timeout,
            pending: run
        );

        var (success, _, error) = await run;

        Assert.False(condition: success);
        Assert.Equal(
            actual: error,
            expected: "timed out after 3 seconds"
        );
    }
}
