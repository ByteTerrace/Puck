using System.Collections.Concurrent;
using Puck.Assets;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class ShaderCompilerTests {
    [Fact]
    public async Task Dxc_diagnostics_name_the_authored_source_and_its_line() {
        using var fixture = new Fixture();
        var runner = new FakeRunner { DxcDiagnostics = true };
        var sourcePath = Path.Combine(
            path1: fixture.Path,
            path2: "bad.hlsl"
        );
        var result = await new ShaderCompiler(
            fixture.Path,
            runner
        ).CompileAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            descriptor: new ShaderCompilationRequest(
                name: "bad",
                stages: [new ShaderStageSource(
                    ShaderStage.Compute,
                    sourcePath,
                    "[numthreads(8,8,1)] void main() {\n syntax\n}"
                )]
            )
        );

        Assert.False(condition: result.IsSuccess);
        Assert.All(
            collection: result.Diagnostics.Where(predicate: static d => (d.Message == "syntax error")),
            action: diagnostic => {
                Assert.True(condition: diagnostic.IsError);
                Assert.Equal(
                    2,
                    diagnostic.Line
                );
                Assert.Equal(
                    5,
                    diagnostic.Column
                );
                Assert.Equal(
                    ShaderStage.Compute,
                    diagnostic.Stage
                );
                Assert.Equal(
                    sourcePath,
                    diagnostic.Path
                );
            }
        );
        Assert.Contains(
            collection: result.Diagnostics,
            filter: static d => (d.Message == "syntax error")
        );
    }
    [Fact]
    public async Task Cancellation_is_forwarded_to_the_tool_process() {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner { Block = true };
        var compiler = new ShaderCompiler(
            fixture.Path,
            runner
        );
        var request = new ShaderCompilationRequest(
            name: "cancel",
            stages: [new ShaderStageSource(
                    ShaderStage.Compute,
                    "cancel.hlsl",
                    "void main() { }"
                )]
        );
        var task = compiler.CompileAsync(
            request,
            cancellation.Token
        );

        await runner.Started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: async () => await task);
    }
    [Fact]
    public async Task Concurrent_identical_compiles_do_not_race_cache_publication() {
        using var fixture = new Fixture();
        var runner = new FakeRunner();
        var compiler = new ShaderCompiler(
            fixture.Path,
            runner
        );
        var request = new ShaderCompilationRequest(
            name: "concurrent",
            stages: [new ShaderStageSource(
                    ShaderStage.Compute,
                    "concurrent.hlsl",
                    "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }"
                )]
        );

        var results = await Task.WhenAll(tasks: Enumerable.Range(
            count: 8,
            start: 0
        ).Select(selector: _ => compiler.CompileAsync(
            request,
            TestContext.Current.CancellationToken
        )));

        Assert.All(
            results,
            result => Assert.True(condition: result.IsSuccess)
        );
        Assert.Equal(
            2,
            runner.Calls.Count
        );
    }
    [Fact]
    public async Task Hlsl_compute_and_graphics_stages_compile_to_both_backends() {
        using var fixture = new Fixture();
        var runner = new FakeRunner();
        var request = new ShaderCompilationRequest(
            name: "graphics",
            stages: [
            new ShaderStageSource(
                    EntryPoint: "main",
                    Path: "vertex.hlsl",
                    Source: "float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }",
                    Stage: ShaderStage.Vertex
                ),
            new ShaderStageSource(
                    EntryPoint: "main",
                    Path: "fragment.hlsl",
                    Source: "float4 main() : SV_Target { return 1; }",
                    Stage: ShaderStage.Fragment
                )]
        );

        var result = await new ShaderCompiler(
            fixture.Path,
            runner
        ).CompileAsync(
            request,
            TestContext.Current.CancellationToken
        );

        Assert.True(condition: result.IsSuccess);
        Assert.Equal(
            2,
            result.SpirvByStage.Count
        );
        Assert.Equal(
            2,
            result.DxilByStage.Count
        );
        Assert.Equal(
            4,
            runner.Calls.Count
        );
        Assert.Contains(
            collection: runner.Calls,
            filter: call => call.Arguments.Contains(value: "vs_6_6")
        );
        Assert.Contains(
            collection: runner.Calls,
            filter: call => call.Arguments.Contains(value: "ps_6_6")
        );
    }
    [Fact]
    public async Task Include_edits_invalidate_cache_and_identical_compiles_share_one_tool_run() {
        using var fixture = new Fixture();
        var includePath = Path.Combine(
            path1: fixture.Path,
            path2: "shared.hlsl"
        );
        var sourcePath = Path.Combine(
            path1: fixture.Path,
            path2: "main.hlsl"
        );

        File.WriteAllText(
            contents: "#define VALUE 1",
            path: includePath
        );
        const string Source = "#include \"shared.hlsl\"\n[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }";
        var request = new ShaderCompilationRequest(
            name: "cached",
            stages: [new ShaderStageSource(
                    ShaderStage.Compute,
                    sourcePath,
                    Source
                )]
        );
        var runner = new FakeRunner();
        var compiler = new ShaderCompiler(
            fixture.Path,
            runner
        );
        var first = await compiler.CompileAsync(
            request,
            TestContext.Current.CancellationToken
        );
        var second = await compiler.CompileAsync(
            request,
            TestContext.Current.CancellationToken
        );

        Assert.True(condition: first.IsSuccess);
        Assert.True(condition: second.IsSuccess);
        Assert.Equal(
            2,
            runner.Calls.Count
        );
        File.WriteAllText(
            contents: "#define VALUE 2",
            path: includePath
        );
        var third = await compiler.CompileAsync(
            request,
            TestContext.Current.CancellationToken
        );

        Assert.True(condition: third.IsSuccess);
        Assert.Equal(
            4,
            runner.Calls.Count
        );
    }
    /// <summary>Separate compiler instances share no gate, so racing them over one cache directory is the same race
    /// separate processes run: the file system is all they have in common. Each round is a fresh key that every
    /// racer builds at once while a reader keeps opening whatever the cache has published.</summary>
    [Fact]
    public async Task Separate_compiler_instances_racing_one_key_all_succeed_and_publish_only_complete_entries() {
        const int Racers = 4;
        const int Rounds = 48;
        using var fixture = new Fixture();
        var cancellationToken = TestContext.Current.CancellationToken;
        var compilers = Enumerable.Range(
            count: Racers,
            start: 0
        ).Select(selector: _ => new ShaderCompiler(
            fixture.Path,
            new FakeRunner()
        )).ToArray();
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);
        var incomplete = new ConcurrentBag<string>();
        var reader = Task.Run(
            action: () => {
                while (!reading.IsCancellationRequested) {
                    foreach (var marker in Directory.GetFiles(
                        fixture.Path,
                        "*.complete",
                        SearchOption.TopDirectoryOnly
                    )) {
                        var stem = marker[..^".complete".Length];

                        foreach (var target in ((string[])["spv", "dxil"])) {
                            var bytecode = $"{stem}.comp.{target}";

                            if (
                                !File.Exists(path: bytecode) ||
                                (AtomicFile.ReadAllBytes(path: bytecode).Length != 4)
                            ) { incomplete.Add(item: bytecode); }
                        }
                    }
                }
            },
            cancellationToken: cancellationToken
        );

        for (var round = 0; (round < Rounds); round++) {
            var request = new ShaderCompilationRequest(
                name: "shared",
                stages: [
                new ShaderStageSource(
                        ShaderStage.Compute,
                        Path.Combine(
                            path1: fixture.Path,
                            path2: "shared.hlsl"
                        ),
                        $"[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) {{ }} // round {round}"
                    )
            ]
            );
            var results = await Task.WhenAll(tasks: compilers.Select(selector: compiler => compiler.CompileAsync(
                cancellationToken: cancellationToken,
                descriptor: request
            )));

            Assert.All(
                results,
                result => Assert.True(condition: result.IsSuccess)
            );
        }
        await reading.CancelAsync();
        await reader;

        Assert.Empty(collection: incomplete);
        Assert.Equal(
            Rounds,
            Directory.GetFiles(
                fixture.Path,
                "*.complete",
                SearchOption.TopDirectoryOnly
            ).Length
        );
        Assert.Empty(collection: Directory.GetFiles(
            fixture.Path,
            "*.tmp",
            SearchOption.AllDirectories
        ));
        Assert.Empty(collection: Directory.GetDirectories(
            fixture.Path,
            ".build-*",
            SearchOption.TopDirectoryOnly
        ));
    }

    private sealed class Fixture : IDisposable {
        public Fixture() {
            Path = System.IO.Path.Combine(
            path1: System.IO.Path.GetTempPath(),
            path2: ("puck-shader-tests-" + Guid.NewGuid().ToString(format: "N"))
        ); Directory.CreateDirectory(path: Path);
        }

        public string Path { get; }

        public void Dispose() {
            try {
                Directory.Delete(
            path: Path,
            recursive: true
        );
            } catch (IOException) { }
        }
    }
    private sealed class FakeRunner : IShaderProcessRunner {
        public readonly ConcurrentBag<(string FileName, string Arguments)> Calls = [];
        public readonly TaskCompletionSource Started = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Block { get; init; }
        public bool DxcDiagnostics { get; init; }

        private static string? Output(IReadOnlyList<string> arguments) {
            for (var i = 0; (i < (arguments.Count - 1)); i++) {
                if (arguments[i] is "-Fo") { return arguments[(i + 1)]; }
            }
            return null;
        }

        public async Task<ChildProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
            Calls.Add(item: (fileName, string.Join(
                separator: " ",
                values: arguments
            )));
            Started.TrySetResult();
            if (Block) {
                await Task.Delay(
                cancellationToken: cancellationToken,
                delay: Timeout.InfiniteTimeSpan
            );
            }
            var output = Output(arguments: arguments);

            if (output is not null) {
                Directory.CreateDirectory(path: System.IO.Path.GetDirectoryName(path: output)!);
                File.WriteAllBytes(
                    bytes: [1, 2, 3, 4],
                    path: output
                );

            }
            if (DxcDiagnostics) {
                return new ChildProcessResult(
                    ExitCode: 1,
                    Stderr: $"{arguments[^1]}:2:5: error: syntax error",
                    Stdout: string.Empty
                );
            }
            return new ChildProcessResult(
                ExitCode: 0,
                Stderr: string.Empty,
                Stdout: string.Empty
            );
        }
    }
}
