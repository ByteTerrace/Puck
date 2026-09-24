using System.Diagnostics;

namespace Puck.HumbleGamingBrick.Tests;

/// <summary>Runs the battery's gate lane the way a build agent does — the real executable, its exit code, and the files
/// it leaves behind — so <c>dotnet test</c> over the solution covers the emulator. Corpus stages skip when the corpora
/// have not been fetched into the local cache; a skip is not a pass on those rows, only the self-contained stages
/// are then proven.</summary>
public sealed class GateLaneTests {
    // Drain both pipes while the child runs. Cancellation must retire the child as well as the waiting task;
    // disposing Process alone only closes our handle and otherwise leaves the battery consuming CPU.
    private static async Task<(string Output, string Error)> WaitForExitAndDrainAsync(Process process, CancellationToken cancellation) {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        OperationCanceledException? cancelled = null;

        try {
            await process.WaitForExitAsync(cancellationToken: cancellation);
        } catch (OperationCanceledException exception) {
            cancelled = exception;
        } finally {
            if (!process.HasExited) {
                try {
                    process.Kill(entireProcessTree: true);
                } catch (InvalidOperationException) when (process.HasExited) {
                    // The child completed between observing it and sending the termination request.
                }
            }
            await process.WaitForExitAsync(cancellationToken: CancellationToken.None);
            await Task.WhenAll(
                stdout,
                stderr
            );
        }
        if (cancelled is not null) {
            throw new OperationCanceledException(
                $"Battery cancelled after cleanup.\n{await stdout}\n{await stderr}",
                cancelled,
                cancellation
            );
        }
        return (await stdout, await stderr);
    }

    [Fact]
    public async Task CancellingTheWaitRetiresTheChildProcess() {
        // The actual battery is already built alongside this test assembly. Cancel its pending wait as soon as
        // it starts; this exercises process ownership without a platform-specific shell or another fixture exe.
        var startInfo = new ProcessStartInfo {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.RequireRoot(),
        };

        startInfo.ArgumentList.Add(item: Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Puck.HumbleGamingBrick.Post.dll"
        ));
        startInfo.ArgumentList.Add(item: "--lane");
        startInfo.ArgumentList.Add(item: "gate");
        startInfo.ArgumentList.Add(item: "--artifacts");
        startInfo.ArgumentList.Add(item: Path.Combine(
            path1: Path.GetTempPath(),
            path2: "puck-gb-post-cancellation",
            path3: Guid.NewGuid().ToString(format: "N")
        ));
        using var process = Process.Start(startInfo: startInfo)!;
        using var cancellation = new CancellationTokenSource();
        var waiting = WaitForExitAndDrainAsync(
            process,
            cancellation.Token
        );

        try {
            Assert.False(
                condition: waiting.IsCompleted,
                userMessage: "The cancellation control requires a running child."
            );
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => waiting);
            Assert.True(
                condition: process.HasExited,
                userMessage: "Cancelling a test must also retire its child process."
            );
        } finally {
            cancellation.Cancel();
            await ((Task)waiting).ConfigureAwait(options: ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }
    // The battery decides its own verdict however long a loaded host takes to run it. A deadlocked stage cannot
    // observe cancellation, so the battery's hang guard abandons it and exits 2 with the stage named as hung; the
    // wait here ends only with the battery or with the test run itself.
    [Fact]
    public async Task GateLaneIsGreen() {
        var repositoryRoot = RepositoryPaths.RequireRoot();
        var artifacts = Path.Combine(
            path1: Path.GetTempPath(),
            path2: "puck-gb-post-gate",
            path3: Guid.NewGuid().ToString(format: "N")
        );
        var startInfo = new ProcessStartInfo {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot,
        };

        foreach (var argument in new[] {
            Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Puck.HumbleGamingBrick.Post.dll"
        ),
            "--lane",
            "gate",
            "--artifacts",
            artifacts,
        }) {
            startInfo.ArgumentList.Add(item: argument);
        }

        using var process = (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: "dotnet did not start"));
        var cancellation = TestContext.Current.CancellationToken;

        var (stdout, stderr) = await WaitForExitAndDrainAsync(
            cancellation: cancellation,
            process: process
        );

        var report = Path.Combine(
            path1: artifacts,
            path2: "post-report.txt"
        );

        Assert.True(
            condition: (process.ExitCode == 0),
            userMessage: $"gate lane exited {process.ExitCode}\n{(File.Exists(path: report)
            ? await File.ReadAllTextAsync(
                    cancellationToken: cancellation,
                    path: report
                )
            : stdout)}\n{stderr}"
        );
        Assert.True(condition: File.Exists(path: Path.Combine(
            path1: artifacts,
            path2: "results.junit.xml"
        )));
        Assert.True(condition: File.Exists(path: Path.Combine(
            path1: artifacts,
            path2: "Expectations.candidate.json"
        )));
    }
}
