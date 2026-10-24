using System.Diagnostics;
using Puck.Testing;

namespace Puck.AdvancedGamingBrick.Tests;

/// <summary>Runs the battery through its real executable with an empty corpus cache, as CI's test shards see it whatever
/// this machine has fetched: every self-contained stage runs on hand-assembled vectors and synthetic cartridges, with no
/// BIOS image or ROM asset, and every corpus stage skips. CI's battery job proves the corpus rows.</summary>
public sealed class SelfContainedBatteryTests {
    /// <summary>Verifies the self-contained stages pass and the run leaves its report.</summary>
    /// <returns>The task that completes after the executable exits and its outputs are checked.</returns>
    [Fact]
    public async Task SelfContainedStagesAreGreen() {
        using var directory = new TemporaryDirectory(prefix: "puck-agb-post-");

        var artifacts = directory.PathOf(name: "artifacts");
        var start = new ProcessStartInfo {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryPaths.RequireRoot(),
        };

        foreach (var argument in new[] {
            Path.Combine(path1: AppContext.BaseDirectory, path2: "Puck.AdvancedGamingBrick.Post.dll"),
            "--artifacts", artifacts, "--corpus-cache", directory.PathOf(name: "no-corpora"),
        }) {
            start.ArgumentList.Add(item: argument);
        }
        using var process = Process.Start(startInfo: start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The battery's hang guard ends a deadlocked stage; a cancelled test run still retires the child here.
        try {
            await process.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken);
        } finally {
            if (!process.HasExited) {
                try {
                    process.Kill(entireProcessTree: true);
                } catch (InvalidOperationException) when (process.HasExited) {
                }
            }
            await process.WaitForExitAsync(cancellationToken: CancellationToken.None);
        }
        var stdout = await output;
        var stderr = await error;

        Assert.True(condition: (process.ExitCode == 0), userMessage: $"battery exit {process.ExitCode}\n{stdout}\n{stderr}");
        Assert.Contains(actualString: stdout, expectedSubstring: "PASS  queued-host-time-travel");
        Assert.True(condition: File.Exists(path: Path.Combine(path1: artifacts, path2: "results.junit.xml")));
    }
}
