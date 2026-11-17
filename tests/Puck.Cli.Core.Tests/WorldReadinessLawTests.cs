using System.Diagnostics;
using System.Globalization;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Core.Tests;

/// <summary>A failed readiness mark stays retryable until a later readiness report publishes it.</summary>
public sealed class WorldReadinessLawTests {
    [Fact]
    public void AnUnwritableMarkDirectoryIsRetriedOnTheNextReadinessLine() {
        using var checkout = new TemporaryDirectory();

        GitScratchCheckout.Initialize(repository: checkout.RootPath);
        using var process = Process.GetCurrentProcess();
        var folder = checkout.PathOf(name: ".claude/leases/world-ready");

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: folder)!);
        File.WriteAllText(contents: "blocks directory creation", path: folder);
        var readiness = WorldReadiness.For(process: process, opensDevice: true, checkout: checkout.RootPath);
        var mark = Path.Combine(path1: folder, path2: string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"{process.Id}-{process.StartTime.ToUniversalTime().Ticks}"));

        readiness.Observe(line: "[engine: ready at tick 1]", stdout: false);
        Assert.False(condition: readiness.Ready);
        Assert.False(condition: File.Exists(path: mark));

        File.Delete(path: folder);
        readiness.Observe(line: "[bakes: settled at tick 2]", stdout: false);
        Assert.True(condition: readiness.Ready);
        Assert.True(condition: File.Exists(path: mark));
    }
}
