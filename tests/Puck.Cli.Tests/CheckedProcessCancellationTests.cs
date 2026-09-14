using System.Diagnostics;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class CheckedProcessCancellationTests {
    [Fact]
    public async Task CancellationTerminatesTheOwnedChildBeforeReturning() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-process-cancellation-");
        var marker = Path.Combine(
            path1: directory.FullName,
            path2: "child.pid"
        );
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);
        Task<string>? run = null;

        try {
            string executable;
            string[] arguments;

            if (OperatingSystem.IsWindows()) {
                executable = "powershell.exe";
                // The generated working directory carries the PID file; no user text enters shell code.
                arguments = ["-NoProfile", "-NonInteractive", "-Command", "[System.IO.File]::WriteAllText('child.pid', $PID.ToString()); Start-Sleep -Seconds 120"];
            } else {
                executable = "/bin/sh";
                arguments = ["-c", "echo $$ > child.pid; exec sleep 120"];
            }
            run = CliProcess.RunCheckedAsync(
                directory.FullName,
                executable,
                arguments,
                capture: true,
                cancellationToken: cancelled.Token
            );
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

            timeout.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 20));
            var pid = 0;

            while (
                !File.Exists(path: marker) ||
                !int.TryParse(
                File.ReadAllText(path: marker),
                out pid
            )
            ) { await Task.Delay(
                20,
                timeout.Token
            ); }
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => run);
            try {
                using var child = Process.GetProcessById(processId: pid);

                Assert.True(condition: child.HasExited);
            } catch (ArgumentException) { /* The operating system already reaped the owned child. */ }
        } finally {
            await cancelled.CancelAsync();
            if (run is not null) {
                try { await run; } catch (OperationCanceledException) { }
            }
            directory.Delete(recursive: true);
        }
    }
}
