using System.Diagnostics;
using System.Text.Json;

namespace Puck.HumbleGamingDeck.Tests;

/// <summary>Runs Tier A through the battery executable and checks its emitted reports.</summary>
public sealed class GateLaneTests {
    /// <summary>Verifies every Tier A stage without an external corpus.</summary>
    /// <returns>The task that completes after the executable exits and its reports are checked.</returns>
    [Fact]
    public async Task TierAIsGreenThroughTheRealBatteryExecutable() {
        var artifacts = Path.Combine(path1: Path.GetTempPath(), path2: "puck-hgd-post", path3: Guid.NewGuid().ToString(format: "N"));
        var start = new ProcessStartInfo {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = RepositoryPaths.RequireRoot(),
        };

        foreach (var argument in new[] {
            Path.Combine(path1: AppContext.BaseDirectory, path2: "Puck.HumbleGamingDeck.Post.dll"),
            "--tier", "A", "--artifacts", artifacts,
        }) {
            start.ArgumentList.Add(item: argument);
        }
        using var process = Process.Start(startInfo: start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);

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

        Assert.True(condition: (process.ExitCode == 0), userMessage: $"Tier A exit {process.ExitCode}\n{stdout}\n{stderr}");
        Assert.DoesNotContain(actualString: stdout, expectedSubstring: "SKIP");
        Assert.Contains(actualString: stdout, expectedSubstring: "mid-instruction-replay");
        Assert.Contains(actualString: stdout, expectedSubstring: "cpu-interrupts-rdy");
        Assert.True(condition: File.Exists(path: Path.Combine(path1: artifacts, path2: "results.junit.xml")));
        using var summary = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(path1: artifacts, path2: "summary.json")));

        Assert.Equal(expected: JsonValueKind.Object, actual: summary.RootElement.ValueKind);
    }
}
