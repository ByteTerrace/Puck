using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class ShaderBuildLawTests {
    [InlineData("truncate")]
    [InlineData("payload")]
    [InlineData("key")]
    [Theory]
    public async Task ADamagedCacheEntryIsRecompiledBeforeAnyCheckoutCanPublishIt(string damage) {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-integrity-");
        var cache = Path.Combine(path1: scratch.RootPath, path2: "cache");
        var checkout = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "one"));
        var runner = new Runner();

        Assert.True(condition: await Build(cache: cache, checkout: checkout, runner: runner));
        var output = checkout.Outputs[0];
        var compiler = new ShaderCompiler(cacheDirectory: cache, processRunner: runner);
        var plan = compiler.Plan(stage: new ShaderStageSource(EntryPoint: "CSMain", Path: output.SourcePath, Source: File.ReadAllText(path: output.SourcePath), Stage: output.Stage), target: output.Target);
        var entry = Path.Combine(path1: cache, path2: (plan.Key + ".spv"));
        var bytes = File.ReadAllBytes(path: entry);

        if (damage == "truncate") {
            bytes = bytes[..(bytes.Length / 2)];
        } else {
            bytes[((damage == "key") ? 0 : (bytes.Length - 1))] ^= 1;
        }
        File.WriteAllBytes(bytes: bytes, path: entry);
        Assert.False(condition: compiler.IsCached(plan: plan));
        Assert.Null(@object: compiler.ReadCached(plan: plan));

        // Even a current bytecode/sidecar pair cannot bless a damaged cache entry.
        runner.Clear();
        Assert.True(condition: await Build(cache: cache, checkout: checkout, runner: runner));
        Assert.Equal(expected: 1, actual: runner.Runs);
        Assert.Equal(expected: File.ReadAllBytes(path: output.SourcePath), actual: compiler.ReadCached(plan: plan));

        var other = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "two"));

        Assert.True(condition: await Build(cache: cache, checkout: other, runner: runner));
        Assert.Equal(expected: 1, actual: runner.Runs);
        Assert.Equal(expected: File.ReadAllBytes(path: output.SourcePath), actual: File.ReadAllBytes(path: other.Outputs[0].OutputPath));
    }
    [Fact]
    public async Task AnUnexpectedCompileExceptionJoinsEveryPeerBeforeReturningItsCores() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-join-");
        var checkout = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "one"));
        var runner = new UnwindingRunner();
        var cores = new FixedShaderCoreBroker(cores: 3);
        var text = new StringWriter();
        var log = TextWriter.Synchronized(writer: text);
        // Memory is plentiful, so the three compiles start together whatever this machine has free.
        var build = new ShaderBuild(
            availableMemory: static () => (64L << 30),
            compiler: new ShaderCompiler(cacheDirectory: Path.Combine(path1: scratch.RootPath, path2: "cache"), processRunner: runner, toolchainDirectory: ToolchainStandIn(root: scratch.RootPath)),
            lockFile: Path.Combine(path1: checkout.Root, path2: "obj/shader-publish.lock"),
            log: log,
            projectDirectory: checkout.Root
        );
        var compile = build.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, cores: cores, outputs: checkout.Outputs);

        try {
            var first = await Task.WhenAny(task1: runner.Unwinding.Task, task2: compile).WaitAsync(timeout: TimeSpan.FromSeconds(seconds: 30), cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(condition: (first == runner.Unwinding.Task), userMessage: $"The build ended before any compiler child unwound, with {runner.Started} of 3 started ({compile.Exception?.GetBaseException().Message}):\n{text}");
            _ = await Task.WhenAny(task1: compile, task2: Task.Delay(millisecondsDelay: 200, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(condition: compile.IsCompleted, userMessage: "The build returned while a compiler child was still unwinding.");
            Assert.Equal(expected: 3, actual: cores.Held);
        } finally {
            runner.Finish.TrySetResult();
            _ = await Record.ExceptionAsync(testCode: async () => await compile);
        }
        Assert.IsType<IOException>(@object: await Record.ExceptionAsync(testCode: async () => await compile));
        Assert.Equal(expected: 0, actual: cores.Held);
        Assert.Equal(expected: 0, actual: runner.Active);
        Assert.All(collection: checkout.Outputs, action: static output => Assert.False(condition: File.Exists(path: output.OutputPath)));
    }

    private sealed class UnwindingRunner : IShaderProcessRunner {
        private int m_started;
        private int m_active;

        private readonly TaskCompletionSource m_allStarted = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Unwinding { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public int Active => Volatile.Read(location: ref m_active);
        public int Started => Volatile.Read(location: ref m_started);

        public async Task<ChildProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
            Interlocked.Increment(location: ref m_active);
            if (Interlocked.Increment(location: ref m_started) == 3) { m_allStarted.TrySetResult(); }
            try {
                // Every compile is admitted at once, so all three arrive; none observes the build's cancellation before its
                // blocking phase, where unwinding is what the law watches.
                await m_allStarted.Task;
                if (Path.GetFileName(path: arguments[^1]) == "a.comp.hlsl") {
                    throw new IOException(message: "Deliberate unexpected compiler failure.");
                }
                try {
                    await Task.Delay(cancellationToken: cancellationToken, delay: Timeout.InfiniteTimeSpan);
                } finally {
                    Unwinding.TrySetResult();
                    await Finish.Task;
                }
                throw new InvalidOperationException(message: "The blocking compiler unexpectedly finished.");
            } finally {
                Interlocked.Decrement(location: ref m_active);
            }
        }
    }
}
