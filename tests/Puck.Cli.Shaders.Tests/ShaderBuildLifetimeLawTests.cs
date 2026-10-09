using System.Reflection;
using Microsoft.Build.Framework;
using Xunit;

namespace Puck.Cli.Shaders.Tests;

/// <summary>The shader build task keeps each request file until its own generator exits, stops admission and joins
/// its compiler children on cancellation, ends the generator on an initial core refusal, and never makes an
/// uncancellable second request of the build engine.</summary>
public sealed class ShaderBuildLifetimeLawTests : ShaderBuildTargetsLaws {
    [Fact]
    public async Task ConcurrentTasksKeepTheirRequestFilesUntilTheirOwnGeneratorExits() {
        using var fixture = new ShaderBuildFixture();

        fixture.ParallelProject(mode: "hold");
        var firstEngine = DispatchProxy.Create<IBuildEngine9, ShaderCoreEngine>();
        var secondEngine = DispatchProxy.Create<IBuildEngine9, ShaderCoreEngine>();
        using var firstRequested = new ManualResetEventSlim();
        using var secondRequested = new ManualResetEventSlim();

        ((ShaderCoreEngine)firstEngine).Grant = count => { firstRequested.Set(); return count; };
        ((ShaderCoreEngine)secondEngine).Grant = count => { secondRequested.Set(); return count; };
        var first = fixture.CreateBuildTask(engine: firstEngine);
        var second = fixture.CreateBuildTask(engine: secondEngine);
        var firstBuild = Task.Run(function: first.Execute, cancellationToken: TestContext.Current.CancellationToken);
        Task<bool>? secondBuild = null;

        try {
            _ = WaitFor(find: () => (firstRequested.IsSet ? "the first generator read its manifest" : null));
            secondBuild = Task.Run(function: second.Execute, cancellationToken: TestContext.Current.CancellationToken);
            _ = WaitFor(find: () => (secondRequested.IsSet ? "the second generator read its manifest" : null));

            Assert.Equal(expected: 2, actual: Directory.GetFiles(path: fixture.PathOf(path: "obj"), searchPattern: "shader-build.txt*").Length);
        } finally {
            fixture.Write(path: "release", text: "release both generators' compiler children");
            _ = await firstBuild;
            if (secondBuild is not null) { _ = await secondBuild; }
        }

        Assert.True(condition: await firstBuild);
        Assert.True(condition: await secondBuild!);
        Assert.Empty(collection: Directory.GetFiles(path: fixture.PathOf(path: "obj"), searchPattern: "shader-build.txt*"));
        Assert.Equal(expected: 0, actual: ((ShaderCoreEngine)firstEngine).Held);
        Assert.Equal(expected: 0, actual: ((ShaderCoreEngine)secondEngine).Held);
    }
    [Fact]
    public async Task CancellationStopsAdmissionAndJoinsTheActualCompilerChildren() {
        using var fixture = new ShaderBuildFixture();

        fixture.ParallelProject(mode: "hold");
        var engine = DispatchProxy.Create<IBuildEngine9, ShaderCoreEngine>();
        var budget = ((ShaderCoreEngine)engine);

        budget.Grant = _ => 1;
        var compiler = fixture.CreateBuildTask(engine: engine);
        var build = Task.Run(function: compiler.Execute, cancellationToken: TestContext.Current.CancellationToken);

        try {
            _ = WaitFor(find: () => (((fixture.Started().Length > 0) || build.IsCompleted) ? "a compiler child or an early exit" : null));
            Assert.False(condition: build.IsCompleted, userMessage: string.Join(separator: '\n', values: budget.Messages));
            ((ICancelableTask)compiler).Cancel();
            if (await Task.WhenAny(task1: build, task2: Task.Delay(TimeSpan.FromSeconds(value: 30), TestContext.Current.CancellationToken)) != build) {
                fixture.Write(path: "release", text: "watchdog release");
            }
        } finally {
            if (!build.IsCompleted) {
                ((ICancelableTask)compiler).Cancel();
            }
        }

        Assert.False(condition: await build.WaitAsync(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(seconds: 30)));
        Assert.False(condition: File.Exists(path: fixture.PathOf(path: "release")), userMessage: "Cancellation did not join the compiler children before the watchdog released them.");
        // The generator joined its compiles and said so: the host's process-tree kill, its fallback, would leave no line.
        Assert.Contains(collection: budget.Messages, expected: "Shader build cancelled: every compile it started has ended.");
        fixture.RequireChildrenExited();
        Assert.Equal(expected: 0, actual: budget.Held);
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.PathOf(path: "Assets"), searchPattern: "*.hash", searchOption: SearchOption.AllDirectories));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task AnInitialCoreRefusalEndsTheGeneratorWithoutWaitingForCancellation(bool throws) {
        using var fixture = new ShaderBuildFixture();

        fixture.ParallelProject(mode: "normal");
        var engine = DispatchProxy.Create<IBuildEngine9, ShaderCoreEngine>();
        var budget = ((ShaderCoreEngine)engine);
        using var requested = new ManualResetEventSlim();

        budget.Grant = _ => {
            requested.Set();
            return (throws ? throw new InvalidOperationException(message: "deliberate budget refusal") : 0);
        };
        var compiler = fixture.CreateBuildTask(engine: engine);
        var build = Task.Run(function: compiler.Execute, cancellationToken: TestContext.Current.CancellationToken);
        var stoppedWithoutCancellation = false;

        try {
            _ = WaitFor(find: () => ((requested.IsSet || build.IsCompleted) ? "a request or early exit" : null));
            Assert.True(condition: requested.IsSet, userMessage: string.Join(separator: '\n', values: budget.Messages));
            stoppedWithoutCancellation = (await Task.WhenAny(task1: build, task2: Task.Delay(TimeSpan.FromSeconds(value: 10), TestContext.Current.CancellationToken)) == build);
        } finally {
            if (!build.IsCompleted) { ((ICancelableTask)compiler).Cancel(); }
            _ = await build.WaitAsync(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(seconds: 30));
        }

        Assert.True(condition: stoppedWithoutCancellation, userMessage: "The host silently dropped the refusal and left the generator waiting for its first core.");
        Assert.False(condition: await build);
        Assert.Equal(expected: 0, actual: budget.Held);
        Assert.Empty(collection: fixture.Started());
    }
    [Fact]
    public async Task ABuildNeverMakesAnUncancellableSecondEngineRequest() {
        using var fixture = new ShaderBuildFixture();

        fixture.ParallelProject(mode: "normal");
        var engine = DispatchProxy.Create<IBuildEngine9, ShaderCoreEngine>();
        var budget = ((ShaderCoreEngine)engine);
        using var unblock = new ManualResetEventSlim();
        var requests = 0;

        budget.Grant = _ => {
            if (Interlocked.Increment(location: ref requests) > 1) {
                unblock.Wait(cancellationToken: TestContext.Current.CancellationToken);
            }
            return 1;
        };
        var compiler = fixture.CreateBuildTask(engine: engine);
        var build = Task.Run(function: compiler.Execute, cancellationToken: TestContext.Current.CancellationToken);

        try {
            Assert.True(condition: await build.WaitAsync(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(seconds: 30)), userMessage: string.Join(separator: '\n', values: budget.Messages));
        } finally {
            unblock.Set();
            if (!build.IsCompleted) { ((ICancelableTask)compiler).Cancel(); }
            _ = await build.WaitAsync(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(seconds: 30));
        }

        Assert.Equal(actual: requests, expected: 1);
        Assert.Equal(expected: 0, actual: budget.Held);
        Assert.Equal(expected: 3, actual: fixture.Compiles());
    }
}
/// <summary>A build engine stand-in that grants and counts cores and keeps every logged message.</summary>
public class ShaderCoreEngine : DispatchProxy {
    private int m_held;

    public int Held => Volatile.Read(location: ref m_held);

    public Func<int, int> Grant { get; set; } = static count => count;
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
        switch (targetMethod!.Name) {
            case "RequestCores":
                var granted = Grant(arg: ((int)args![0]!));

                _ = Interlocked.Add(location1: ref m_held, value: granted);
                return granted;
            case "ReleaseCores":
                var held = Interlocked.Add(location1: ref m_held, value: -((int)args![0]!));

                Assert.True(condition: (held >= 0), userMessage: "The task released cores it did not hold.");
                return null;
            case "LogErrorEvent":
            case "LogMessageEvent":
            case "LogWarningEvent":
                Messages.Enqueue(item: (((BuildEventArgs)args![0]!).Message ?? ""));
                return null;
            default:
                return (targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(type: targetMethod.ReturnType) : null);
        }
    }
}
