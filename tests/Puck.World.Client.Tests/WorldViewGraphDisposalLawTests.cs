using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A graph host joins every compilation before its owner releases the compiler's directory, including a
/// compilation superseded or removed on an earlier frame. A held tool writes its final output after cancellation;
/// disposal waits for that output and the compiler's cleanup.</summary>
public sealed class WorldViewGraphDisposalLawTests {
    [InlineData("pending")]
    [InlineData("superseded")]
    [InlineData("removed")]
    [Theory]
    public async Task ADisposedHostWritesNothingMoreUnderItsCompilerDirectory(string state) {
        using var directory = new TemporaryDirectory();
        var tools = directory.PathOf(name: "tools");

        Directory.CreateDirectory(path: tools);
        File.WriteAllBytes(bytes: [], path: Path.Combine(path1: tools, path2: "dxc.exe"));
        File.WriteAllText(
            contents: "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { output[id.xy] = 0; }",
            path: directory.PathOf(name: "held.hlsl")
        );
        var runner = new HeldRunner();
        var cache = directory.PathOf(name: "pipelines");
        using var host = new WorldViewGraphHost(
            documentDirectory: directory.RootPath,
            packager: new ShaderPackager(compiler: new ShaderCompiler(
                cacheDirectory: cache,
                processRunner: runner,
                toolchainDirectory: tools
            ))
        );
        using var instances = FakeGraphInstances.Attach(
            create: static name => new ShaderPipelineRenderNode(
                deviceContext: new RefusingGpuDevice(),
                height: 4,
                hostsOnDirectX: false,
                name: name,
                pipelines: new GpuPassPipelineCache(),
                width: 4
            ),
            host: host
        );
        Task? disposal = null;

        try {
            host.Reconcile(views: new WorldViewDefaults(Graphs: [new WorldViewGraph(Name: "held", Source: "held.hlsl")]));
            await runner.Entered.Task.WaitAsync(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken);

            if (state == "superseded") {
                host.QueueCompile(name: "held", source: "missing.hlsl");
            } else if (state == "removed") {
                host.Reconcile(views: new WorldViewDefaults(Graphs: []));
            }

            disposal = Task.Run(action: host.Dispose, cancellationToken: TestContext.Current.CancellationToken);
            await runner.Canceled.Task.WaitAsync(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken);

            // The tool is canceled but has not returned. Disposal must still be joining it, even if its entry is gone.
            var first = await Task.WhenAny(
                task1: disposal,
                task2: Task.Delay(millisecondsDelay: 100, cancellationToken: TestContext.Current.CancellationToken)
            );

            Assert.NotSame(actual: first, expected: disposal);
            runner.Release.TrySetResult();
            await disposal.WaitAsync(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: 1, actual: runner.Writes);
            Assert.Empty(collection: Directory.EnumerateDirectories(path: cache, searchPattern: ".build-*"));
            // The final tool write and the compiler's cleanup both precede disposal, so deleting the cache is safe.
            Directory.Delete(path: cache, recursive: true);
        } finally {
            runner.Release.TrySetResult();
            if (disposal is not null) {
                await disposal.WaitAsync(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken);
            }
            host.Dispose();
            if (runner.Entered.Task.IsCompleted) {
                await runner.Exited.Task.WaitAsync(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken);
            }
        }
    }

    private sealed class HeldRunner : IShaderProcessRunner {
        public TaskCompletionSource Entered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public int Writes { get; private set; }

        public async Task<ChildProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
            Entered.TrySetResult();
            try {
                await Task.Delay(cancellationToken: cancellationToken, delay: Timeout.InfiniteTimeSpan);
            } catch (OperationCanceledException) {
                Canceled.TrySetResult();
                await Release.Task;
                var output = arguments[(arguments.ToList().IndexOf(item: "-Fo") + 1)];

                File.WriteAllBytes(bytes: [1, 2, 3, 4], path: output);
                Writes++;
                throw;
            } finally {
                Exited.TrySetResult();
            }

            throw new InvalidOperationException(message: "The held tool only returns after cancellation.");
        }
    }
}
