using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class ShaderBuildLawTests {
    [Fact]
    public async Task TwoInterleavedPublishersLeaveABytecodeAndSidecarOfOneGeneration() {
        Assert.SkipUnless(condition: OperatingSystem.IsWindows(), reason: "Stalling publication requires Windows mandatory delete sharing.");
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-publication-");
        var checkout = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "one"));
        var cache = Path.Combine(path1: scratch.RootPath, path2: "cache");
        var runner = new Runner();

        Assert.True(condition: await Build(cache: cache, checkout: checkout, runner: runner));
        var output = checkout.Outputs[^1];
        var sidecar = (output.OutputPath + ".hash");
        using var firstRunner = new PublicationRunner();
        using var secondRunner = new PublicationRunner();
        var tools = ToolchainStandIn(root: scratch.RootPath);

        ShaderBuild Publisher(PublicationRunner compiler) => new(
            compiler: new ShaderCompiler(cacheDirectory: cache, processRunner: compiler, toolchainDirectory: tools),
            lockFile: Path.Combine(path1: checkout.Root, path2: "obj/shader-publish.lock"),
            log: TextWriter.Null,
            projectDirectory: checkout.Root);

        File.WriteAllText(path: output.SourcePath, contents: "generation A");
        var first = Publisher(compiler: firstRunner).CompileAsync(outputs: [output], cores: new FixedShaderCoreBroker(cores: 1), cancellationToken: TestContext.Current.CancellationToken);

        await firstRunner.Started.Task.WaitAsync(timeout: TimeSpan.FromSeconds(seconds: 30), cancellationToken: TestContext.Current.CancellationToken);
        File.WriteAllText(path: output.SourcePath, contents: "generation B");
        var second = Publisher(compiler: secondRunner).CompileAsync(outputs: [output], cores: new FixedShaderCoreBroker(cores: 1), cancellationToken: TestContext.Current.CancellationToken);

        await secondRunner.Started.Task.WaitAsync(timeout: TimeSpan.FromSeconds(seconds: 30), cancellationToken: TestContext.Current.CancellationToken);
        using var oldSidecar = new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: sidecar, share: FileShare.Read);
        FileStream? stagedSidecar = null;

        try {
            firstRunner.Finish.SetResult();
            TestLiveness.Until(reason: () => "Publisher A did not stage its commit record.", step: () => {
                var path = Directory.EnumerateFiles(path: Path.GetDirectoryName(path: sidecar)!, searchPattern: (Path.GetFileName(path: sidecar) + ".*.tmp")).SingleOrDefault();

                if (path is null) { return false; }
                try { stagedSidecar = new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: path, share: FileShare.Read); return true; } catch (IOException) { return false; }
            });
            oldSidecar.Dispose();
            TestLiveness.Until(reason: () => "Publisher A did not remove the old commit record.", step: () => !File.Exists(path: sidecar));
            secondRunner.Finish.SetResult();
            await secondRunner.Finished.Task.WaitAsync(timeout: TimeSpan.FromSeconds(seconds: 30), cancellationToken: TestContext.Current.CancellationToken);
            _ = await Task.WhenAny(task1: second, task2: Task.Delay(millisecondsDelay: 300, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(condition: second.IsCompleted, userMessage: "Publisher B completed while A's transaction held an unpublished sidecar.");
        } finally {
            stagedSidecar?.Dispose();
            oldSidecar.Dispose();
            firstRunner.Finish.TrySetResult();
            secondRunner.Finish.TrySetResult();
            _ = await Task.WhenAll(first, second);
        }

        Assert.True(condition: await first);
        Assert.True(condition: await second);
        Assert.Equal(expected: "generation B", actual: File.ReadAllText(path: output.OutputPath));
        Assert.True(condition: Publisher(compiler: firstRunner).Check(outputs: [output]));
    }

    // A one-byte dxc stand-in: the runners never start it, and hashing it keeps each compile's key cheap, so a publisher's
    // compile stays well inside the bounded retries of the peer transaction a law holds open.
    private static string ToolchainStandIn(string root) {
        var tools = Path.Combine(path1: root, path2: "tools");

        _ = Directory.CreateDirectory(path: tools);
        File.WriteAllBytes(bytes: [0], path: Path.Combine(path1: tools, path2: ShaderCompiler.DxcTool));

        return tools;
    }

    private sealed class PublicationRunner : IShaderProcessRunner, IDisposable {
        public TaskCompletionSource Started { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ChildProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
            Started.TrySetResult();
            await Finish.Task.WaitAsync(cancellationToken: cancellationToken);
            var output = arguments[(arguments.ToList().IndexOf(item: "-Fo") + 1)];

            File.WriteAllBytes(path: output, bytes: File.ReadAllBytes(path: arguments[^1]));
            Finished.TrySetResult();
            return new ChildProcessResult(ExitCode: 0, Stdout: "", Stderr: "");
        }
        public void Dispose() => Finish.TrySetResult();
    }
}
