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

    // The launch records the profile it expects of a World, and the World's own report of baking meshes makes its store cold,
    // so the peak the bake reaches is learned under the cold profile whatever the launch predicted.
    [Fact]
    public void AWorldThatReportsBakingMarksItsProfileCold() {
        using var checkout = new TemporaryDirectory();

        GitScratchCheckout.Initialize(repository: checkout.RootPath);
        using var process = Process.GetCurrentProcess();
        var root = Path.Combine(path1: checkout.RootPath, path2: ".claude/leases");
        var running = new Dictionary<int, long> { [process.Id] = process.StartTime.ToUniversalTime().Ticks };
        var warm = new HostStartProfile(Kind: HostStartKind.WindowedWorld, Device: "vulkan");
        var readiness = WorldReadiness.For(process: process, opensDevice: true, checkout: checkout.RootPath, profile: warm);

        Assert.Equal(expected: warm, actual: StartProfiles.Among(root: root, running: running)[(process.Id, running[process.Id])]);

        // A boot that loads every mesh it needs bakes none.
        readiness.Observe(line: "[sdf.mesh-bake] start residency=a groups=4 baking=0 loading=4 shared=0", stdout: false);
        Assert.Equal(expected: warm, actual: StartProfiles.Among(root: root, running: running)[(process.Id, running[process.Id])]);

        readiness.Observe(line: "[sdf.mesh-bake] start residency=a groups=4 baking=3 loading=1 shared=0", stdout: false);
        Assert.Equal(expected: warm.Cold, actual: StartProfiles.Among(root: root, running: running)[(process.Id, running[process.Id])]);
    }
}
