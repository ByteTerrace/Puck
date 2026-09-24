using System.Diagnostics;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class CheckedProcessCancellationTests {
    // The child publishes its PID in the name of a file it creates, and the watcher exists before the child starts, so
    // the creation is an event the test awaits rather than a file it polls for. The event carries the name, so the
    // test never opens the file: a scanner or indexer that briefly holds a fresh file open cannot fail a read.
    [Fact]
    public async Task CancellationTerminatesTheOwnedChildBeforeReturning() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-process-cancellation-");
        var published = new TaskCompletionSource<int>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(
            filter: "child-*.pid",
            path: directory.FullName
        );

        void Publish(object sender, FileSystemEventArgs change) {
            if (int.TryParse(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var childPid,
                s: Path.GetFileNameWithoutExtension(path: change.Name)?["child-".Length..]
            )) {
                _ = published.TrySetResult(result: childPid);
            }
        }

        watcher.Created += Publish;
        watcher.Renamed += Publish;
        watcher.EnableRaisingEvents = true;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);
        Task<string>? run = null;
        int? pid = null;

        try {
            string executable;
            string[] arguments;

            if (OperatingSystem.IsWindows()) {
                executable = "powershell.exe";
                // The generated working directory carries the PID file; no user text enters shell code.
                arguments = ["-NoProfile", "-NonInteractive", "-Command", "[System.IO.File]::Create('child-' + $PID + '.pid').Dispose(); Start-Sleep -Seconds 120"];
            } else {
                executable = "/bin/sh";
                // exec keeps the shell's PID for sleep, so the published PID is the child that must be killed.
                arguments = ["-c", "touch child-$$.pid; exec sleep 120"];
            }
            run = CliProcess.RunCheckedAsync(
                workingDirectory: directory.FullName,
                fileName: executable,
                arguments: arguments,
                capture: true,
                cancellationToken: cancelled.Token
            );
            // A child that exits without publishing surfaces its own failure instead of leaving the wait unanswered.
            if (await Task.WhenAny(
                task1: published.Task,
                task2: run
            ).WaitAsync(cancellationToken: TestContext.Current.CancellationToken) == run) {
                Assert.Fail(message: $"the child exited before publishing its PID: {await run}");
            }
            pid = await published.Task;

            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => run);
            try {
                using var child = Process.GetProcessById(processId: pid.Value);

                Assert.True(condition: child.HasExited, userMessage: "the cancelled run returned while its child was still running");
            } catch (ArgumentException) { /* The operating system already reaped the owned child. */ }
        } finally {
            await cancelled.CancelAsync();
            if (run is not null) {
                try { await run; } catch (OperationCanceledException) { }
            }
            // A child the product failed to kill still holds the directory; the test ends it so the verdict above stands.
            if (pid is { } leaked) {
                try {
                    using var child = Process.GetProcessById(processId: leaked);

                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(cancellationToken: CancellationToken.None);
                } catch (Exception error) when ((error is ArgumentException or InvalidOperationException)) { /* Already gone. */ }
            }
            watcher.EnableRaisingEvents = false;
            directory.Delete(recursive: true);
        }
    }
}
