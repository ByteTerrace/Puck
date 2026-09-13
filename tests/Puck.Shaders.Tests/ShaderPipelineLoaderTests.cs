using System.Text.Json;

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
            ShaderSourceLanguage.Hlsl,
            "main",
            ShaderPipelinePassKind.Compute,
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

        foreach (var pass in passes) { File.WriteAllText(
            Path.Combine(
                path1: fixture.Directory,
                path2: pass.Source
            ),
            "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }"
        ); }
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
        var runner = new Runner { BeforeFirstRun = () => File.AppendAllText(
            contents: "\n// edited during compilation",
            path: source
        ) };
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
        Assert.True(condition: first.RetryRecommended);
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
        Assert.False(condition: second.RetryRecommended);
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
        public int FailCall { get; init; }

        public Task<ShaderProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            m_calls++;
            if (m_calls == 1) { BeforeFirstRun?.Invoke(); }
            if (m_calls == FailCall) { return Task.FromResult(new ShaderProcessResult(
                1,
                string.Empty,
                "deliberate compiler error"
            )); }
            for (var index = 0; ((index + 1) < arguments.Count); index++) {
                if (arguments[index] is "-Fo" or "-o" or "--output") {
                    File.WriteAllBytes(
                        arguments[(index + 1)],
                        [1, 2, 3, 4]
                    );
                    break;
                }
            }
            return Task.FromResult(new ShaderProcessResult(
                0,
                string.Empty,
                string.Empty
            ));
        }
    }
}
