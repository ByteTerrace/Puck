using System.Diagnostics;
using System.Globalization;
using Puck.Cli.Canary;
using Puck.Hosting;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Runs.Tests;

public sealed class AuthorityCompanionLawTests {
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(minutes: 2);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task DisposalDrainsAnInheritedPipeAndPreservesTheTail(bool exitedBeforeDisposal) {
        var clock = new VirtualClock();

        var (executable, arguments) = (OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", "$info = New-Object System.Diagnostics.ProcessStartInfo('powershell.exe', '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 180'); $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $held = [System.Diagnostics.Process]::Start($info); [Console]::Out.WriteLine($held.Id); [Console]::Error.WriteLine('[world.listen: bound loopback]'); $null = [Console]::ReadLine(); [Console]::Out.Write('tail'); [Console]::Error.Write('error')" })
            : ("/bin/sh", new[] { "-c", "sleep 180 & printf '%s\\n' $!; printf '[world.listen: bound loopback]\\n' >&2; read quit; printf tail; printf error >&2" }));
        var process = ChildProcess.StartRedirected(fileName: executable, arguments: arguments);
        var companion = new AuthorityCompanion(clock: clock, process: process, quitInput: "quit\n");
        Task? disposal = null;

        try {
            Assert.True(condition: companion.WaitUntilListening(timeout: HangGuard));
            if (exitedBeforeDisposal) {
                await process.StandardInput.WriteLineAsync(buffer: "quit".AsMemory(), cancellationToken: Token);
                await process.StandardInput.FlushAsync(cancellationToken: Token);
                await process.WaitForExitAsync(cancellationToken: Token).WaitAsync(timeout: HangGuard, cancellationToken: Token);
            }
            disposal = Task.Run(action: companion.Dispose, cancellationToken: Token);
            await clock.ExpireAsync(dueTime: ChildProcess.ExitDrainGrace, pending: disposal, ct: Token).WaitAsync(timeout: HangGuard, cancellationToken: Token);
            await disposal.WaitAsync(timeout: HangGuard, cancellationToken: Token);

            Assert.EndsWith(expectedEndString: $"tail{Environment.NewLine}", actualString: companion.Stdout);
            Assert.Equal(expected: $"[world.listen: bound loopback]{Environment.NewLine}error{Environment.NewLine}", actual: companion.Stderr);
            Assert.Equal(expected: ChildProcess.ExitDrainGrace, actual: clock.Elapsed);
        } finally {
            var firstLine = companion.Stdout.Split(separator: '\n')[0].Trim();

            if (int.TryParse(s: firstLine, provider: CultureInfo.InvariantCulture, result: out var pid)) {
                using var inheritor = Process.GetProcessById(processId: pid);

                if (!inheritor.HasExited) { inheritor.Kill(); }
                await inheritor.WaitForExitAsync(cancellationToken: Token);
            }
            if (disposal is null) {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); }
                companion.Dispose();
            } else {
                await disposal.WaitAsync(timeout: HangGuard, cancellationToken: Token);
            }
        }
    }
}
