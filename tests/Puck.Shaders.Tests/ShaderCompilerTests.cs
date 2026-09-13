using System.Collections.Concurrent;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

public sealed class ShaderCompilerTests {
    [Fact]
    public async Task Adapter_diagnostics_map_to_author_lines() {
        using var fixture = new Fixture();
        var runner = new FakeRunner { GlslDiagnostics = true };
        const string Source = "void mainImage(out vec4 c, in vec2 p) {\n c = vec4(1);\n}";
        var adapter = ShadertoyShaderAdapter.Adapt(Source);
        var result = await new ShaderCompiler(
            fixture.Path,
            runner
        ).CompileAsync(
            "bad",
            "bad.glsl",
            Source,
            TestContext.Current.CancellationToken
        );

        var diagnostic = Assert.Single(
            result.Diagnostics,
            d => (d.IsError && (d.Message == "syntax error"))
        );

        Assert.Equal(
            2,
            diagnostic.Line
        );
        Assert.Equal(
            ShaderStage.Compute,
            diagnostic.Stage
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
            "cancel",
            [new ShaderStageSource(
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
    public async Task Compiler_returns_channel_validation_as_a_failed_candidate() {
        using var fixture = new Fixture();
        var request = ShaderCompilationRequest.Compute(
            "bad-channel",
            Path.Combine(
                path1: fixture.Path,
                path2: "bad.glsl"
            ),
            "void mainImage(out vec4 c, in vec2 p) { c = texture(iChannel0, p); }",
            channels: new Dictionary<string, uint>()
        );
        var result = await new ShaderCompiler(
            fixture.Path,
            new FakeRunner()
        ).CompileAsync(
            request,
            TestContext.Current.CancellationToken
        );

        Assert.False(result.IsSuccess);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => (diagnostic.IsError && diagnostic.Message.Contains(
                "iChannel0",
                StringComparison.Ordinal
            ))
        );
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
            "concurrent",
            [new ShaderStageSource(
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
            "graphics",
            [
            new ShaderStageSource(
                    ShaderStage.Vertex,
                    "vertex.hlsl",
                    "float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }",
                    ShaderSourceLanguage.Hlsl,
                    "main"
                ),
            new ShaderStageSource(
                    ShaderStage.Fragment,
                    "fragment.hlsl",
                    "float4 main() : SV_Target { return 1; }",
                    ShaderSourceLanguage.Hlsl,
                    "main"
                )]
        );

        var result = await new ShaderCompiler(
            fixture.Path,
            runner
        ).CompileAsync(
            request,
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSuccess);
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
            "cached",
            [new ShaderStageSource(
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
    [Fact]
    public async Task Separate_compiler_instances_publish_only_complete_shared_cache_entries() {
        using var fixture = new Fixture();
        var firstRunner = new FakeRunner();
        var secondRunner = new FakeRunner();
        var firstCompiler = new ShaderCompiler(
            fixture.Path,
            firstRunner
        );
        var secondCompiler = new ShaderCompiler(
            fixture.Path,
            secondRunner
        );
        var request = new ShaderCompilationRequest(
            "shared",
            [
            new ShaderStageSource(
                    ShaderStage.Compute,
                    Path.Combine(
                        path1: fixture.Path,
                        path2: "shared.hlsl"
                    ),
                    "[numthreads(8,8,1)] void main(uint3 id : SV_DispatchThreadID) { }"
                )
        ]
        );

        var results = await Task.WhenAll(
            firstCompiler.CompileAsync(
                request,
                TestContext.Current.CancellationToken
            ),
            secondCompiler.CompileAsync(
                request,
                TestContext.Current.CancellationToken
            )
        );

        Assert.All(
            results,
            result => Assert.True(condition: result.IsSuccess)
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
    [Fact]
    public async Task Shadertoy_channels_and_float_output_are_adapted() {
        using var fixture = new Fixture();
        var runner = new FakeRunner();
        var request = ShaderCompilationRequest.Compute(
            "channels",
            "channels.glsl",
            "void mainImage(out vec4 c, in vec2 p) { c = texture(iChannelNoise, p); }",
            channels: new Dictionary<string, uint> { ["iChannelNoise"] = 4 },
            outputFormat: GpuPixelFormat.R16G16B16A16Float
        );

        var result = await new ShaderCompiler(
            fixture.Path,
            runner
        ).CompileAsync(
            request,
            TestContext.Current.CancellationToken
        );
        var source = File.ReadAllText(path: Directory.GetFiles(
            path: fixture.Path,
            searchPattern: "*.source"
        ).Single());

        Assert.True(result.IsSuccess);
        Assert.Contains(
            actualString: source,
            expectedSubstring: "binding = 4"
        );
        Assert.Contains(
            actualString: source,
            expectedSubstring: "rgba16f"
        );
        Assert.Contains(
            actualString: source,
            expectedSubstring: "sampler2D iChannelNoise"
        );
        Assert.Equal(
            3,
            runner.Calls.Count
        );
    }
    [Fact]
    public void Translated_native_glsl_registers_follow_descriptor_kind_and_order() {
        const string Translated = "Texture2D<float4> source : register(t7, space0); SamplerState sourceSampler : register(s7, space0); RWStructuredBuffer<float4> output : register(u9, space0);";
        var remapped = ShaderCompiler.RemapTranslatedHlslRegisters(
            Translated,
            [
            new ShaderDescriptorBinding(
                    7,
                    GpuComputeBindingKind.SampledImage
                ),
            new ShaderDescriptorBinding(
                    3,
                    GpuComputeBindingKind.StorageBufferRead
                ),
            new ShaderDescriptorBinding(
                    9,
                    GpuComputeBindingKind.StorageImage
                ),
        ]
        );

        Assert.Contains(
            "register(t0, space0)",
            remapped
        );
        Assert.Contains(
            "register(s0, space0)",
            remapped
        );
        Assert.Contains(
            "register(u0, space0)",
            remapped
        );
    }
    [Fact]
    public void Translated_shadertoy_registers_follow_dense_descriptor_order_and_keep_unused_slots() {
        const string Translated = "RWTexture2D<float4> puckShaderImage : register(u0, space0); Texture2D<float4> iChannel0 : register(t1, space0); SamplerState s0 : register(s1, space0); Texture2D<float4> iChannel1 : register(t3, space0); SamplerState s1 : register(s3, space0);";
        var remapped = ShaderCompiler.RemapTranslatedHlslRegisters(
            Translated,
            new Dictionary<string, uint> {
            ["iChannel0"] = 1,
            ["iChannel1"] = 3,
            ["iChannelUnused"] = 5,
        }
        );

        Assert.Contains(
            "register(t0, space0)",
            remapped
        );
        Assert.Contains(
            "register(s0, space0)",
            remapped
        );
        Assert.Contains(
            "register(t1, space0)",
            remapped
        );
        Assert.Contains(
            "register(s1, space0)",
            remapped
        );
        Assert.Contains(
            "register(u0, space0)",
            remapped
        );
    }

    private sealed class Fixture : IDisposable {
        public Fixture() { Path = System.IO.Path.Combine(
            path1: System.IO.Path.GetTempPath(),
            path2: ("puck-shader-tests-" + Guid.NewGuid().ToString(format: "N"))
        ); Directory.CreateDirectory(path: Path); }

        public string Path { get; }

        public void Dispose() { try { Directory.Delete(
            path: Path,
            recursive: true
        ); } catch (IOException) { } }
    }
    private sealed class FakeRunner : IShaderProcessRunner {
        public readonly ConcurrentBag<(string FileName, string Arguments)> Calls = [];
        public readonly TaskCompletionSource Started = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Block { get; init; }
        public bool GlslDiagnostics { get; init; }

        private static string? Output(IReadOnlyList<string> arguments) {
            for (var i = 0; (i < (arguments.Count - 1)); i++) {
                if (arguments[i] is "-o" or "-Fo" or "--output") { return arguments[(i + 1)]; }
            }
            return null;
        }

        public async Task<ShaderProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
            Calls.Add(item: (fileName, string.Join(
                separator: " ",
                values: arguments
            )));
            Started.TrySetResult();
            if (Block) { await Task.Delay(
                cancellationToken: cancellationToken,
                delay: Timeout.InfiniteTimeSpan
            ); }
            var output = Output(arguments: arguments);

            if (output is not null) {
                Directory.CreateDirectory(path: System.IO.Path.GetDirectoryName(path: output)!);
                File.WriteAllBytes(
                    bytes: [1, 2, 3, 4],
                    path: output
                );
                if (output.EndsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "translated.hlsl"
                )) { File.WriteAllText(
                    contents: "float4 main() : SV_Target { return 1; }",
                    path: output
                ); }
            }
            if (
                GlslDiagnostics &&
                fileName.Contains(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "glslang"
            )
            ) {
                var sourceFile = arguments[^1];
                var prefix = ShadertoyShaderAdapter.Adapt("void mainImage(out vec4 c, in vec2 p) {\n c = vec4(1);\n}").PrefixLineCount;

                return new ShaderProcessResult(
                    1,
                    $"ERROR: {sourceFile}:{(prefix + 2)}: syntax error",
                    string.Empty
                );
            }
            return new ShaderProcessResult(
                0,
                string.Empty,
                string.Empty
            );
        }
    }
}
