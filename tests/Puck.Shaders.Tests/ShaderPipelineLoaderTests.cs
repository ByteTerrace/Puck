using System.Text.Json;

namespace Puck.Shaders.Tests;

public sealed class ShaderPipelineLoaderTests {
    [Fact]
    public void An_edit_during_compilation_refuses_the_whole_candidate_and_allows_retry() {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Directory, "pass.hlsl");
        File.WriteAllText(source, "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }");
        var runner = new Runner { BeforeFirstRun = () => File.AppendAllText(source, "\n// edited during compilation") };
        var loader = new ShaderPipelineLoader(new ShaderCompiler(Path.Combine(fixture.Directory, "cache"), runner));
        var first = loader.Load("edit", source, TestContext.Current.CancellationToken);
        Assert.Null(first.Pipeline);
        Assert.True(first.RetryRecommended);
        Assert.Contains(source, first.Dependencies);
        var second = loader.Load("edit", source, TestContext.Current.CancellationToken);
        Assert.NotNull(second.Pipeline);
        Assert.False(second.RetryRecommended);
    }

    [Fact]
    public void A_broken_middle_pass_does_not_return_a_partial_pipeline() {
        using var fixture = new Fixture();
        var resources = new[] { Image("one"), Image("two"), Image("three") };
        var passes = new[] {
            Pass("one", [], ["one"]), Pass("two", ["one"], ["two"]), Pass("three", ["two"], ["three"]),
        };
        foreach (var pass in passes) { File.WriteAllText(Path.Combine(fixture.Directory, pass.Source), "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }"); }
        var path = Path.Combine(fixture.Directory, "graph.pipeline.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new ShaderPipelineDefinition("graph", resources, passes, ["three"]),
            ShaderPipelineJsonContext.Default.ShaderPipelineDefinition));
        var runner = new Runner { FailCall = 3 };
        var loader = new ShaderPipelineLoader(new ShaderCompiler(Path.Combine(fixture.Directory, "cache"), runner));
        var result = loader.Load("graph", path, TestContext.Current.CancellationToken);
        Assert.Null(result.Pipeline);
        Assert.Contains("failed pass 'two'", result.Message);
        Assert.Contains(Path.Combine(fixture.Directory, "three.hlsl"), result.Dependencies);
    }

    private static ShaderPipelineResource Image(string name) => new(name, Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative());
    private static ShaderPipelinePass Pass(string name, IReadOnlyList<ResourceReference> inputs, IReadOnlyList<ResourceReference> outputs) =>
        new(name, name + ".hlsl", ShaderSourceLanguage.Hlsl, "main", ShaderPipelinePassKind.Compute, inputs, outputs);

    private sealed class Fixture : IDisposable {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "puck-loader-tests-" + Guid.NewGuid().ToString("N"));
        public Fixture() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
    private sealed class Runner : IShaderProcessRunner {
        private int m_calls;
        public Action? BeforeFirstRun { get; init; }
        public int FailCall { get; init; }
        public Task<ShaderProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            m_calls++;
            if (m_calls == 1) { BeforeFirstRun?.Invoke(); }
            if (m_calls == FailCall) { return Task.FromResult(new ShaderProcessResult(1, string.Empty, "deliberate compiler error")); }
            for (var index = 0; index + 1 < arguments.Count; index++) {
                if (arguments[index] is "-Fo" or "-o" or "--output") {
                    File.WriteAllBytes(arguments[index + 1], [1, 2, 3, 4]);
                    break;
                }
            }
            return Task.FromResult(new ShaderProcessResult(0, string.Empty, string.Empty));
        }
    }
}
