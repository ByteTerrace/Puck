using System.Collections.Concurrent;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

public sealed class ShaderCompilerTests
{
    [Fact]
    public async Task Hlsl_compute_and_graphics_stages_compile_to_both_backends()
    {
        using var fixture = new Fixture();
        var runner = new FakeRunner();
        var request = new ShaderCompilationRequest("graphics", [
            new ShaderStageSource(ShaderStage.Vertex, "vertex.hlsl", "float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }", ShaderSourceLanguage.Hlsl, "main"),
            new ShaderStageSource(ShaderStage.Fragment, "fragment.hlsl", "float4 main() : SV_Target { return 1; }", ShaderSourceLanguage.Hlsl, "main")]);

        var result = await new ShaderCompiler(fixture.Path, runner).CompileAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.SpirvByStage.Count);
        Assert.Equal(2, result.DxilByStage.Count);
        Assert.Equal(4, runner.Calls.Count);
        Assert.Contains(runner.Calls, call => call.Arguments.Contains("vs_6_6"));
        Assert.Contains(runner.Calls, call => call.Arguments.Contains("ps_6_6"));
    }

    [Fact]
    public async Task Shadertoy_channels_and_float_output_are_adapted()
    {
        using var fixture = new Fixture();
        var runner = new FakeRunner();
        var request = ShaderCompilationRequest.Compute("channels", "channels.glsl", "void mainImage(out vec4 c, in vec2 p) { c = texture(iChannelNoise, p); }", channels: new Dictionary<string, uint> { ["iChannelNoise"] = 4 }, outputFormat: GpuPixelFormat.R16G16B16A16Float);

        var result = await new ShaderCompiler(fixture.Path, runner).CompileAsync(request, TestContext.Current.CancellationToken);
        var source = File.ReadAllText(Directory.GetFiles(fixture.Path, "*.source").Single());

        Assert.True(result.IsSuccess);
        Assert.Contains("binding = 4", source);
        Assert.Contains("rgba16f", source);
        Assert.Contains("sampler2D iChannelNoise", source);
        Assert.Equal(3, runner.Calls.Count);
    }

    [Fact]
    public async Task Adapter_diagnostics_map_to_author_lines()
    {
        using var fixture = new Fixture();
        var runner = new FakeRunner { GlslDiagnostics = true };
        const string source = "void mainImage(out vec4 c, in vec2 p) {\n c = vec4(1);\n}";
        var adapter = ShadertoyShaderAdapter.Adapt(source);
        var result = await new ShaderCompiler(fixture.Path, runner).CompileAsync("bad", "bad.glsl", source, TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.IsError && d.Message == "syntax error");
        Assert.Equal(2, diagnostic.Line);
        Assert.Equal(ShaderStage.Compute, diagnostic.Stage);
    }

    [Fact]
    public async Task Include_edits_invalidate_cache_and_identical_compiles_share_one_tool_run()
    {
        using var fixture = new Fixture();
        var includePath = Path.Combine(fixture.Path, "shared.hlsl");
        var sourcePath = Path.Combine(fixture.Path, "main.hlsl");
        File.WriteAllText(includePath, "#define VALUE 1");
        const string source = "#include \"shared.hlsl\"\n[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }";
        var request = new ShaderCompilationRequest("cached", [new ShaderStageSource(ShaderStage.Compute, sourcePath, source)]);
        var runner = new FakeRunner();
        var compiler = new ShaderCompiler(fixture.Path, runner);
        var first = await compiler.CompileAsync(request, TestContext.Current.CancellationToken);
        var second = await compiler.CompileAsync(request, TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, runner.Calls.Count);
        File.WriteAllText(includePath, "#define VALUE 2");
        var third = await compiler.CompileAsync(request, TestContext.Current.CancellationToken);
        Assert.True(third.IsSuccess);
        Assert.Equal(4, runner.Calls.Count);
    }

    [Fact]
    public async Task Concurrent_identical_compiles_do_not_race_cache_publication()
    {
        using var fixture = new Fixture();
        var runner = new FakeRunner();
        var compiler = new ShaderCompiler(fixture.Path, runner);
        var request = new ShaderCompilationRequest("concurrent", [new ShaderStageSource(ShaderStage.Compute, "concurrent.hlsl", "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }")]);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => compiler.CompileAsync(request, TestContext.Current.CancellationToken)));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task Cancellation_is_forwarded_to_the_tool_process()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner { Block = true };
        var compiler = new ShaderCompiler(fixture.Path, runner);
        var request = new ShaderCompilationRequest("cancel", [new ShaderStageSource(ShaderStage.Compute, "cancel.hlsl", "void main() { }")]);
        var task = compiler.CompileAsync(request, cancellation.Token);
        await runner.Started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "puck-shader-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private sealed class FakeRunner : IShaderProcessRunner
    {
        public readonly ConcurrentBag<(string FileName, string Arguments)> Calls = [];
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block { get; init; }
        public bool GlslDiagnostics { get; init; }
        public async Task<ShaderProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add((fileName, string.Join(" ", arguments)));
            Started.TrySetResult();
            if (Block) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            var output = Output(arguments);
            if (output is not null)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
                File.WriteAllBytes(output, [1, 2, 3, 4]);
                if (output.EndsWith("translated.hlsl", StringComparison.Ordinal)) { File.WriteAllText(output, "float4 main() : SV_Target { return 1; }"); }
            }
            if (GlslDiagnostics && fileName.Contains("glslang", StringComparison.OrdinalIgnoreCase))
            {
                var sourceFile = arguments[^1];
                var prefix = ShadertoyShaderAdapter.Adapt("void mainImage(out vec4 c, in vec2 p) {\n c = vec4(1);\n}").PrefixLineCount;
                return new ShaderProcessResult(1, $"ERROR: {sourceFile}:{prefix + 2}: syntax error", string.Empty);
            }
            return new ShaderProcessResult(0, string.Empty, string.Empty);
        }
        private static string? Output(IReadOnlyList<string> arguments)
        {
            for (var i = 0; i < arguments.Count - 1; i++)
            {
                if (arguments[i] is "-o" or "-Fo" or "--output") { return arguments[i + 1]; }
            }
            return null;
        }
    }
}