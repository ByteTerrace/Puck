using System.Collections.Concurrent;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for a project's shader build (<see cref="ShaderBuild"/>) over a fake DXC: an output whose closure, options and
/// toolchain any checkout compiled is published from the cache and runs no tool, wherever the checkout lies; only the
/// outputs whose closure holds an edited include compile again; a fresh cache compiles everything however current the
/// tree is; DXC reads each source's exact bytes, its include directives as written, so a snapshot compiles to what the
/// source compiles to in place; the compiles run on the cores the broker grants and never more, the longest first; and
/// a failure stops the build and publishes nothing that did not finish.
/// </summary>
public sealed partial class ShaderBuildLawTests {
    [Fact]
    public async Task DxcReadsEachSourcesExactBytesWithItsIncludeDirectivesAsWritten() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-build-");
        // The stand-in DXC writes the text it was handed, so each output is the snapshot of its stage source.
        var runner = new Runner();
        var checkout = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "one"));

        Assert.True(condition: await Build(cache: Path.Combine(path1: scratch.RootPath, path2: "cache"), checkout: checkout, runner: runner));
        foreach (var output in checkout.Outputs) {
            Assert.Equal(expected: File.ReadAllBytes(path: output.SourcePath), actual: File.ReadAllBytes(path: output.OutputPath));
        }
    }
    [Fact]
    public async Task AnOutputAnyCheckoutCompiledIsPublishedFromTheCacheInAnotherCheckoutWithoutATool() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-build-");
        var cache = Path.Combine(path1: scratch.RootPath, path2: "cache");
        var runner = new Runner();
        var one = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "one"));
        var two = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "elsewhere", path3: "two"));

        Assert.True(condition: await Build(cache: cache, checkout: one, runner: runner));
        Assert.Equal(expected: 3, actual: runner.Runs);

        // The same files in another checkout: every output comes from the cache, byte for byte, and no tool runs.
        var build = BuildOf(cache: cache, checkout: two, runner: runner);

        Assert.True(condition: await build.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, cores: new FixedShaderCoreBroker(cores: 4), outputs: two.Outputs));
        Assert.Equal(expected: 3, actual: runner.Runs);
        Assert.Empty(collection: build.Compiled);
        Assert.Equal(expected: 3, actual: build.Restored.Count);
        foreach (var (left, right) in one.Outputs.Zip(second: two.Outputs)) {
            Assert.Equal(expected: File.ReadAllBytes(path: left.OutputPath), actual: File.ReadAllBytes(path: right.OutputPath));
        }

        // A current tree publishes nothing again.
        var again = BuildOf(cache: cache, checkout: two, runner: runner);

        Assert.True(condition: await again.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, cores: new FixedShaderCoreBroker(cores: 4), outputs: two.Outputs));
        Assert.Empty(collection: again.Compiled);
        Assert.Empty(collection: again.Restored);
        Assert.True(condition: again.Check(outputs: two.Outputs));
    }
    [Fact]
    public async Task AnIncludeEditCompilesOnlyTheOutputsWhoseClosureHoldsIt() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-build-");
        var cache = Path.Combine(path1: scratch.RootPath, path2: "cache");
        var runner = new Runner();
        var checkout = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "one"));

        Assert.True(condition: await Build(cache: cache, checkout: checkout, runner: runner));
        runner.Clear();

        File.WriteAllText(contents: "#define A_ONLY 2\n", path: Path.Combine(path1: checkout.Root, path2: "Assets/Shaders/a-only.hlsli"));
        var build = BuildOf(cache: cache, checkout: checkout, runner: runner);

        Assert.True(condition: await build.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, cores: new FixedShaderCoreBroker(cores: 4), outputs: checkout.Outputs));
        Assert.Equal(expected: ["a.comp.spv"], actual: build.Compiled.Select(selector: static output => Path.GetFileName(path: output.OutputPath)));
        Assert.Equal(expected: ["a.comp.hlsl"], actual: runner.Inputs);

        runner.Clear();
        File.WriteAllText(contents: "#define SHARED 2\n", path: Path.Combine(path1: checkout.Root, path2: "Assets/Shaders/shared.hlsli"));
        Assert.True(condition: await Build(cache: cache, checkout: checkout, runner: runner));
        Assert.Equal(expected: ["a.comp.hlsl", "b.comp.hlsl"], actual: runner.Inputs.Order(comparer: StringComparer.Ordinal));
    }
    [Fact]
    public async Task AnEmptyCacheCompilesEveryOutputAgainHoweverCurrentTheTree() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-build-");
        var runner = new Runner();
        var checkout = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "one"));

        Assert.True(condition: await Build(cache: Path.Combine(path1: scratch.RootPath, path2: "first"), checkout: checkout, runner: runner));
        runner.Clear();

        var build = BuildOf(cache: Path.Combine(path1: scratch.RootPath, path2: "second"), checkout: checkout, runner: runner);

        Assert.True(condition: await build.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, cores: new FixedShaderCoreBroker(cores: 4), outputs: checkout.Outputs));
        Assert.Equal(expected: 3, actual: build.Compiled.Count);
        Assert.Equal(expected: 3, actual: runner.Runs);
    }
    [Fact]
    public async Task CompilesRunOnTheGrantedCoresAndNoMoreAndTheBuildEndsHoldingNone() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-build-");
        var runner = new Runner { Delay = TimeSpan.FromMilliseconds(milliseconds: 100) };
        var checkout = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "one"), extra: 5);
        var cores = new FixedShaderCoreBroker(cores: 2);
        var build = BuildOf(cache: Path.Combine(path1: scratch.RootPath, path2: "cache"), checkout: checkout, runner: runner);

        Assert.True(condition: await build.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, cores: cores, outputs: checkout.Outputs));
        Assert.Equal(expected: 8, actual: build.Compiled.Count);
        Assert.Equal(expected: 2, actual: runner.PeakConcurrent);
        Assert.Equal(expected: 2, actual: cores.PeakHeld);
        Assert.Equal(expected: 0, actual: cores.Held);
    }
    [Fact]
    public async Task TheLongestOutputsAreAdmittedFirstByRecordedDurationOtherwiseByClosureSize() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-build-");
        var cache = Path.Combine(path1: scratch.RootPath, path2: "cache");
        // b's closure is the smallest, but b takes longest to compile.
        var runner = new Runner { Delays = { ["b.comp.hlsl"] = TimeSpan.FromMilliseconds(milliseconds: 300) } };
        var checkout = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "one"), padding: 4096);
        var first = BuildOf(cache: cache, checkout: checkout, runner: runner);

        Assert.True(condition: await first.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, cores: new FixedShaderCoreBroker(cores: 1), outputs: checkout.Outputs));
        Assert.Equal(expected: ["a.comp.spv", "c.comp.spv", "b.comp.spv"], actual: first.Admitted.Select(selector: static output => Path.GetFileName(path: output.OutputPath)));

        // Every closure edited, so every output compiles again: now the durations the first build recorded order them.
        foreach (var name in ((ReadOnlySpan<string>)["a.comp.hlsl", "b.comp.hlsl", "c.comp.hlsl"])) {
            File.AppendAllText(contents: "\n// edited\n", path: Path.Combine(path1: checkout.Root, path2: "Assets/Shaders", path3: name));
        }

        var second = BuildOf(cache: cache, checkout: checkout, runner: runner);

        Assert.True(condition: await second.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, cores: new FixedShaderCoreBroker(cores: 1), outputs: checkout.Outputs));
        Assert.Equal(expected: "b.comp.spv", actual: Path.GetFileName(path: second.Admitted[0].OutputPath));
    }
    [Fact]
    public async Task AFailedCompileCancelsItsPeersAndPublishesNothingUnfinished() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-build-");
        // a fails once its peers are running, and they would run forever unless cancelled.
        var runner = new Runner { Blocking = true, Delays = { ["a.comp.hlsl"] = TimeSpan.FromMilliseconds(milliseconds: 200) }, Failing = "a.comp.hlsl" };
        var checkout = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "one"));
        var log = new StringWriter();
        var build = BuildOf(cache: Path.Combine(path1: scratch.RootPath, path2: "cache"), checkout: checkout, log: log, runner: runner);

        Assert.False(condition: await build.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, cores: new FixedShaderCoreBroker(cores: 3), outputs: checkout.Outputs));
        Assert.Contains(expectedSubstring: "error PUCKSHADER: deliberate failure", actualString: log.ToString());
        Assert.Empty(collection: build.Compiled);
        Assert.All(collection: checkout.Outputs, action: static output => Assert.False(condition: File.Exists(path: output.OutputPath)));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ACompilerThatExitsCleanlyWithoutOutputFailsTheBuildAndPublishesNothing(bool emptyFile) {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-build-");
        var checkout = Checkout(root: Path.Combine(path1: scratch.RootPath, path2: "one"));
        var cache = Path.Combine(path1: scratch.RootPath, path2: "cache");
        var log = new StringWriter();
        var build = BuildOf(cache: cache, checkout: checkout, log: log, runner: new Runner { Empty = emptyFile, Silent = !emptyFile });

        Assert.False(condition: await build.CompileAsync(cancellationToken: TestContext.Current.CancellationToken, cores: new FixedShaderCoreBroker(cores: 1), outputs: checkout.Outputs));
        Assert.Contains(expectedSubstring: "error PUCKSHADER: dxc (SPIR-V) exited with code 0", actualString: log.ToString());
        Assert.Empty(collection: build.Compiled);
        Assert.Empty(collection: Directory.EnumerateFiles(path: checkout.Root, searchOption: SearchOption.AllDirectories, searchPattern: "*.spv*"));
        Assert.Empty(collection: Directory.EnumerateFiles(path: cache, searchOption: SearchOption.TopDirectoryOnly, searchPattern: "*.spv*"));
    }

    private static Task<bool> Build(string cache, CheckoutFixture checkout, Runner runner) =>
        BuildOf(cache: cache, checkout: checkout, runner: runner).CompileAsync(cancellationToken: TestContext.Current.CancellationToken, cores: new FixedShaderCoreBroker(cores: 4), outputs: checkout.Outputs);
    private static ShaderBuild BuildOf(string cache, CheckoutFixture checkout, Runner runner, TextWriter? log = null) =>
        new(
            compiler: new ShaderCompiler(cacheDirectory: cache, processRunner: runner),
            lockFile: Path.Combine(path1: checkout.Root, path2: "obj", path3: "shader-publish.lock"),
            log: (log ?? TextWriter.Null),
            projectDirectory: checkout.Root
        );
    // Three compute sources: a reaches a-only.hlsli and shared.hlsli, b reaches shared.hlsli, c reaches nothing; each
    // compiles to SPIR-V alone. Extra sources include nothing; padding grows a's and c's closures past b's.
    private static CheckoutFixture Checkout(string root, int extra = 0, int padding = 0) {
        var shaders = Path.Combine(path1: root, path2: "Assets", path3: "Shaders");
        var pad = new string(c: ' ', count: padding);

        Directory.CreateDirectory(path: shaders);
        File.WriteAllText(contents: "#define SHARED 1\n", path: Path.Combine(path1: shaders, path2: "shared.hlsli"));
        File.WriteAllText(contents: "#define A_ONLY 1\n", path: Path.Combine(path1: shaders, path2: "a-only.hlsli"));
        File.WriteAllText(contents: $"#include \"a-only.hlsli\"\n#include \"shared.hlsli\"\n[numthreads(1,1,1)] void CSMain() {{ }}\n//{pad}\n", path: Path.Combine(path1: shaders, path2: "a.comp.hlsl"));
        File.WriteAllText(contents: "#include \"shared.hlsli\"\n[numthreads(1,1,1)] void CSMain() { }\n", path: Path.Combine(path1: shaders, path2: "b.comp.hlsl"));
        File.WriteAllText(contents: $"[numthreads(1,1,1)] void CSMain() {{ }}\n//{pad[..(pad.Length / 2)]}\n", path: Path.Combine(path1: shaders, path2: "c.comp.hlsl"));
        for (var index = 0; (index < extra); index++) {
            File.WriteAllText(contents: $"[numthreads(1,1,1)] void CSMain() {{ }} // {index}\n", path: Path.Combine(path1: shaders, path2: $"extra{index}.comp.hlsl"));
        }

        return new CheckoutFixture(
            Outputs: [.. Directory.GetFiles(path: shaders, searchPattern: "*.comp.hlsl").Order(comparer: StringComparer.Ordinal).Select(selector: static source => new ShaderBuildOutput(
                OutputPath: Path.ChangeExtension(extension: ".spv", path: source),
                SourcePath: source,
                Stage: ShaderStage.Compute,
                Target: ShaderTarget.Spirv
            ))],
            Root: root
        );
    }

    private sealed record CheckoutFixture(string Root, ShaderBuildOutput[] Outputs);
    // Stands in for DXC: writes the bytes it reads as the output, records the source's file name, holds the run for its
    // delay, and fails the one source named; a blocking runner holds every other run until it is cancelled, and a silent
    // one exits as if it succeeded but writes nothing.
    private sealed class Runner : IShaderProcessRunner {
        private readonly ConcurrentQueue<string> m_inputs = new();

        private int m_concurrent;
        private int m_peak;
        private int m_runs;

        public bool Blocking { get; init; }
        public TimeSpan Delay { get; init; }

        public Dictionary<string, TimeSpan> Delays { get; } = new(comparer: StringComparer.Ordinal);

        public bool Empty { get; init; }
        public string? Failing { get; init; }
        public string[] Inputs => [.. m_inputs];
        public int PeakConcurrent => Volatile.Read(location: ref m_peak);
        public int Runs => Volatile.Read(location: ref m_runs);
        public bool Silent { get; init; }

        public void Clear() {
            m_inputs.Clear();
            m_runs = 0;
        }
        public async Task<ChildProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
            var input = arguments[^1];
            var name = Path.GetFileName(path: input);
            var concurrent = Interlocked.Increment(location: ref m_concurrent);

            Interlocked.Increment(location: ref m_runs);
            m_inputs.Enqueue(item: name);
            for (var peak = m_peak; (concurrent > peak); peak = m_peak) {
                _ = Interlocked.CompareExchange(comparand: peak, location1: ref m_peak, value: concurrent);
            }
            try {
                await Task.Delay(cancellationToken: cancellationToken, delay: Delays.GetValueOrDefault(key: name, defaultValue: Delay));
                if (name == Failing) {
                    return new ChildProcessResult(ExitCode: 1, Stderr: $"{input}:1:1: error: deliberate failure", Stdout: string.Empty);
                }
                if (Blocking) {
                    await Task.Delay(cancellationToken: cancellationToken, delay: Timeout.InfiniteTimeSpan);
                }

                var output = arguments[(arguments.ToList().IndexOf(item: "-Fo") + 1)];

                if (Silent) {
                    return new ChildProcessResult(ExitCode: 0, Stderr: string.Empty, Stdout: string.Empty);
                }

                await File.WriteAllBytesAsync(bytes: (Empty ? [] : await File.ReadAllBytesAsync(cancellationToken: cancellationToken, path: input)), cancellationToken: cancellationToken, path: output);

                return new ChildProcessResult(ExitCode: 0, Stderr: string.Empty, Stdout: string.Empty);
            } finally {
                Interlocked.Decrement(location: ref m_concurrent);
            }
        }
    }
}
