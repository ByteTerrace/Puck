using Xunit;

namespace Puck.Cli.Shaders.Tests;

/// <summary>The shader build runs its compiles side by side on the cores the build engine grants, and a failed
/// compile cancels its peer and publishes nothing.</summary>
public sealed class ShaderCompilerConcurrencyLawTests : ShaderBuildTargetsLaws {
    [Fact]
    public async Task CompilesRunConcurrentlyOnTheCoresTheBuildEngineGrants() {
        if (Environment.ProcessorCount < 2) {
            Assert.Skip(reason: "MSBuild grants one core on a one-processor machine, so no two compiles can overlap.");
        }

        using var fixture = new ShaderBuildFixture();

        fixture.ParallelProject(mode: "hold");
        var build = Task.Run(function: () => fixture.Run(target: "Build"), cancellationToken: TestContext.Current.CancellationToken);

        try {
            _ = WaitFor(find: () => (((fixture.Started().Length >= 2) || build.IsCompleted) ? "two compiler children or an early exit" : null));
            Assert.False(condition: build.IsCompleted, userMessage: "The compilers did not hold at the real-process barrier.");
            Assert.True(condition: (fixture.Started().Length >= 2));
            Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.PathOf(path: "Assets"), searchPattern: "*.hash", searchOption: SearchOption.AllDirectories));
        } finally {
            fixture.Write(path: "release", text: "release every compiler child");
            _ = await build;
        }
        fixture.RequireSuccess(run: await build);
        Assert.Equal(expected: 3, actual: fixture.Started().Length);
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
        fixture.RequireSuccess(run: fixture.Run(target: "CollectShaderBytecode"));
    }
    [Fact]
    public async Task AFailedCompileCancelsItsPeerAndPublishesNothing() {
        if (Environment.ProcessorCount < 2) {
            Assert.Skip(reason: "MSBuild grants one core on a one-processor machine, so no peer runs beside the failure.");
        }

        using var fixture = new ShaderBuildFixture();

        fixture.ParallelProject(mode: "fail-peer");
        var run = Task.Run(function: () => fixture.Run(target: "Build"), cancellationToken: TestContext.Current.CancellationToken);

        try {
            _ = WaitFor(find: () => (((fixture.Started().Length >= 2) || run.IsCompleted) ? "two compiler peers or an early exit" : null));
            // Cancellation has an observed process barrier. If it is missing, release the held peer after the
            // liveness bound so the law can inspect the wrong admission/completion rather than hang forever.
            if (await Task.WhenAny(task1: run, task2: Task.Delay(TimeSpan.FromSeconds(value: 30), TestContext.Current.CancellationToken)) != run) { fixture.Write(path: "release", text: "watchdog release"); }
        } finally {
            if (!run.IsCompleted) { fixture.Write(path: "release", text: "release the test-owned compiler peer"); }
        }
        var build = await run;

        Assert.NotEqual(expected: 0, actual: build.ExitCode);
        Assert.False(condition: build.TimedOut, userMessage: (build.Stdout + build.Stderr));
        Assert.Contains(expectedSubstring: "deliberate compiler failure", actualString: (build.Stdout + build.Stderr));
        Assert.False(condition: File.Exists(path: fixture.PathOf(path: "release")), userMessage: "The failing compile did not cancel its held peer; the watchdog released it.");
        fixture.RequireChildrenExited();
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.PathOf(path: "Assets"), searchPattern: "*.hash", searchOption: SearchOption.AllDirectories));
    }
}
