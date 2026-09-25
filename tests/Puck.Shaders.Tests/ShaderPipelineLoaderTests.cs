using System.Text.Json;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed class ShaderPipelineLoaderTests {
    private static ShaderPipelineResource Image(string name) => new(
        name,
        Format: "R8G8B8A8Unorm",
        Dimensions: ShaderPipelineDimensions.Relative()
    );
    private static ShaderPipelinePass Pass(string name, IReadOnlyList<ResourceReference> inputs, IReadOnlyList<ResourceReference> outputs) =>
        new(
            name,
            (name + ".hlsl"),
            "main",
            ShaderPipelineDocumentPassKind.Compute,
            inputs,
            outputs
        );

    [Fact]
    public void A_broken_middle_pass_does_not_return_a_partial_pipeline() {
        using var fixture = new Fixture();
        var resources = new[] { Image(name: "one"), Image(name: "two"), Image(name: "three") };
        var passes = new[] {
            Pass(
            inputs: [],
            name: "one",
            outputs: ["one"]
        ), Pass(
            inputs: ["one"],
            name: "two",
            outputs: ["two"]
        ), Pass(
            inputs: ["two"],
            name: "three",
            outputs: ["three"]
        ),
        };

        foreach (var pass in passes) {
            File.WriteAllText(
            Path.Combine(
                path1: fixture.Directory,
                path2: pass.Source
            ),
            "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }"
        );
        }
        var path = Path.Combine(
            path1: fixture.Directory,
            path2: "graph.pipeline.json"
        );

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new ShaderPipelineDefinition(
                    name: "graph",
                    outputs: ["three"],
                    passes: passes,
                    resources: resources
                ),
                ShaderPipelineJsonContext.Default.ShaderPipelineDefinition
            )
        );
        var runner = new Runner { FailCall = 3 };
        var loader = new ShaderPipelineLoader(compiler: new ShaderCompiler(
            Path.Combine(
                path1: fixture.Directory,
                path2: "cache"
            ),
            runner
        ));
        var result = loader.Load(
            "graph",
            path,
            TestContext.Current.CancellationToken
        );

        Assert.Null(@object: result.Pipeline);
        Assert.Equal(
            expected: ShaderPipelineLoadStatus.Failed,
            actual: result.Status
        );
        Assert.Contains(
            "failed pass 'two'",
            result.Message
        );
        Assert.Contains(
            Path.Combine(
                path1: fixture.Directory,
                path2: "three.hlsl"
            ),
            result.Dependencies
        );
    }
    [Fact]
    public void An_edit_during_compilation_refuses_the_whole_candidate_and_allows_retry() {
        using var fixture = new Fixture();
        var source = Path.Combine(
            path1: fixture.Directory,
            path2: "pass.hlsl"
        );

        File.WriteAllText(
            contents: "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }",
            path: source
        );
        var runner = new Runner {
            BeforeFirstRun = () => File.AppendAllText(
            contents: "\n// edited during compilation",
            path: source
        ),
        };
        var loader = new ShaderPipelineLoader(compiler: new ShaderCompiler(
            Path.Combine(
                path1: fixture.Directory,
                path2: "cache"
            ),
            runner
        ));
        var first = loader.Load(
            "edit",
            source,
            TestContext.Current.CancellationToken
        );

        Assert.Null(@object: first.Pipeline);
        Assert.Equal(
            expected: ShaderPipelineLoadStatus.Retry,
            actual: first.Status
        );
        Assert.Contains(
            source,
            first.Dependencies
        );
        var second = loader.Load(
            "edit",
            source,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(@object: second.Pipeline);
        Assert.Equal(
            expected: ShaderPipelineLoadStatus.Compiled,
            actual: second.Status
        );
    }
    [Fact]
    public void A_missing_compiler_is_unsupported_rather_than_a_failed_candidate() {
        using var fixture = new Fixture();
        var source = Path.Combine(
            path1: fixture.Directory,
            path2: "pass.hlsl"
        );

        File.WriteAllText(
            contents: "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }",
            path: source
        );
        var loader = new ShaderPipelineLoader(compiler: new ShaderCompiler(
            Path.Combine(
                path1: fixture.Directory,
                path2: "cache"
            ),
            new Runner { MissingTool = true }
        ));
        var result = loader.Load(
            "missing",
            source,
            TestContext.Current.CancellationToken
        );

        Assert.Null(@object: result.Pipeline);
        Assert.Equal(
            expected: ShaderPipelineLoadStatus.Unsupported,
            actual: result.Status
        );
        Assert.Contains(
            source,
            result.Dependencies
        );
    }
    [Fact]
    public void Native_tools_see_a_snapshot_whose_length_does_not_grow_with_the_source_depth() {
        using var fixture = new Fixture();
        var deep = Path.Combine(paths: [fixture.Directory, .. Enumerable.Repeat(
            count: 12,
            element: "a-directory-name-of-some-length"
        )]);

        Directory.CreateDirectory(path: deep);
        var source = Path.Combine(
            path1: deep,
            path2: "pass.hlsl"
        );

        File.WriteAllText(
            contents: "#include \"shared.hlsli\"\n[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }",
            path: source
        );
        File.WriteAllText(
            contents: "// shared",
            path: Path.Combine(
                path1: deep,
                path2: "shared.hlsli"
            )
        );
        var cache = Path.Combine(
            path1: fixture.Directory,
            path2: "cache"
        );
        var runner = new Runner();
        var result = new ShaderPipelineLoader(compiler: new ShaderCompiler(
            cache,
            runner
        )).Load(
            "deep",
            source,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            expected: ShaderPipelineLoadStatus.Compiled,
            actual: result.Status
        );
        Assert.NotEmpty(collection: runner.Calls);
        Assert.All(
            collection: runner.Calls.SelectMany(selector: static call => call),
            action: argument => Assert.DoesNotContain(
                actualString: argument,
                expectedSubstring: "a-directory-name-of-some-length"
            )
        );
    }

    private sealed class Fixture : IDisposable {
        public string Directory { get; } = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-loader-tests-" + Guid.NewGuid().ToString(format: "N"))
        );

        public Fixture() => System.IO.Directory.CreateDirectory(path: Directory);

        public void Dispose() => System.IO.Directory.Delete(
            Directory,
            recursive: true
        );
    }
    private sealed class Runner : IShaderProcessRunner {
        private int m_calls;

        public Action? BeforeFirstRun { get; init; }
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public int FailCall { get; init; }
        // Every launch fails the way Process.Start does for an executable absent from the search path.
        public bool MissingTool { get; init; }

        public Task<ChildProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            if (MissingTool) { throw new System.ComponentModel.Win32Exception(message: $"{fileName} was not found"); }
            m_calls++;
            Calls.Add(item: [.. arguments]);
            if (m_calls == 1) { BeforeFirstRun?.Invoke(); }
            if (m_calls == FailCall) {
                return Task.FromResult(result: new ChildProcessResult(
                ExitCode: 1,
                Stderr: "deliberate compiler error",
                Stdout: string.Empty
            ));
            }
            for (var index = 0; ((index + 1) < arguments.Count); index++) {
                if (arguments[index] is "-Fo" or "-o" or "--output") {
                    File.WriteAllBytes(
                        arguments[(index + 1)],
                        [1, 2, 3, 4]
                    );
                    break;
                }
            }
            return Task.FromResult(result: new ChildProcessResult(
                ExitCode: 0,
                Stderr: string.Empty,
                Stdout: string.Empty
            ));
        }
    }
}
