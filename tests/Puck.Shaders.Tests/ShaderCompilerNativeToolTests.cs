using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>Exercises the compiler against the installed DXC/glslang/spirv-cross toolchain.</summary>
public sealed class ShaderCompilerNativeToolTests {
    private static string? Find(params string[] names) {
        var path = Environment.GetEnvironmentVariable(variable: "PATH");

        if (string.IsNullOrWhiteSpace(value: path)) { return null; }
        foreach (var directory in path.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: Path.PathSeparator
        )) {
            foreach (var name in names) {
                var candidate = Path.Combine(
                    path1: directory,
                    path2: name
                );

                if (File.Exists(path: candidate)) { return candidate; }
                if (File.Exists(path: (candidate + ".exe"))) { return (candidate + ".exe"); }
            }
        }
        return null;
    }

    [Fact]
    public async Task Native_tools_compile_hlsl_compute_graphics_and_shadertoy_channels_for_both_backends() {
        var dxc = Find("dxc");
        var glslang = Find(
            "glslangValidator",
            "glslang"
        );
        var cross = Find("spirv-cross");

        Assert.SkipWhen(
            condition: ((dxc is null) || (glslang is null) || (cross is null)),
            reason: "DXC, glslangValidator, and spirv-cross are required for this native compiler test."
        );

        var root = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-shader-native-" + Guid.NewGuid().ToString(format: "N"))
        );

        Directory.CreateDirectory(path: root);
        try {
            var vertexPath = Path.Combine(
                path1: root,
                path2: "native.vert.hlsl"
            );
            var fragmentPath = Path.Combine(
                path1: root,
                path2: "native.frag.hlsl"
            );
            var computePath = Path.Combine(
                path1: root,
                path2: "native.comp.hlsl"
            );
            var toyPath = Path.Combine(
                path1: root,
                path2: "native.glsl"
            );

            await File.WriteAllTextAsync(
                vertexPath,
                "struct Out { float4 position : SV_Position; }; Out main(uint id : SV_VertexID) { Out o; o.position = float4((id == 2 ? 3.0 : -1.0), (id == 1 ? 3.0 : -1.0), 0.0, 1.0); return o; }",
                TestContext.Current.CancellationToken
            );
            await File.WriteAllTextAsync(
                fragmentPath,
                "float4 main() : SV_Target { return float4(0.25, 0.5, 0.75, 1.0); }",
                TestContext.Current.CancellationToken
            );
            await File.WriteAllTextAsync(
                computePath,
                "[numthreads(4, 2, 1)] void customEntry(uint3 id : SV_DispatchThreadID) { }",
                TestContext.Current.CancellationToken
            );
            await File.WriteAllTextAsync(
                toyPath,
                "void mainImage(out vec4 color, in vec2 fragCoord) { color = vec4(gain, bias); color += texture(iChannelHeat, fragCoord / iResolution.xy); }",
                TestContext.Current.CancellationToken
            );

            var compiler = new ShaderCompiler(Path.Combine(
                path1: root,
                path2: "cache"
            ));
            var compute = await compiler.CompileAsync(
                new ShaderCompilationRequest(
                    "native-compute",
                    [
                new ShaderStageSource(
                            ShaderStage.Compute,
                            computePath,
                            await File.ReadAllTextAsync(
                                computePath,
                                TestContext.Current.CancellationToken
                            ),
                            ShaderSourceLanguage.Hlsl,
                            "customEntry",
                            4,
                            2,
                            1
                        )
            ]
                ),
                TestContext.Current.CancellationToken
            );

            Assert.True(
                condition: compute.IsSuccess,
                userMessage: string.Join(
                    separator: Environment.NewLine,
                    values: compute.Diagnostics.Select(selector: static d => d.Message)
                )
            );

            var graphics = await compiler.CompileAsync(
                new ShaderCompilationRequest(
                    "native-graphics",
                    [
                new ShaderStageSource(
                            ShaderStage.Vertex,
                            vertexPath,
                            await File.ReadAllTextAsync(
                                vertexPath,
                                TestContext.Current.CancellationToken
                            ),
                            ShaderSourceLanguage.Hlsl,
                            "main"
                        ),
                new ShaderStageSource(
                            ShaderStage.Fragment,
                            fragmentPath,
                            await File.ReadAllTextAsync(
                                fragmentPath,
                                TestContext.Current.CancellationToken
                            ),
                            ShaderSourceLanguage.Hlsl,
                            "main"
                        )
            ]
                ),
                TestContext.Current.CancellationToken
            );

            Assert.True(
                condition: graphics.IsSuccess,
                userMessage: string.Join(
                    separator: Environment.NewLine,
                    values: graphics.Diagnostics.Select(selector: static d => d.Message)
                )
            );
            Assert.Equal(
                2,
                graphics.SpirvByStage.Count
            );
            Assert.Equal(
                2,
                graphics.DxilByStage.Count
            );
            Assert.NotEmpty(collection: graphics.SpirvByStage[ShaderStage.Vertex].ToArray());
            Assert.NotEmpty(collection: graphics.DxilByStage[ShaderStage.Fragment].ToArray());

            var toy = await compiler.CompileAsync(
                new ShaderCompilationRequest(
                    "native-toy",
                    [
                new ShaderStageSource(
                            ShaderStage.Compute,
                            toyPath,
                            await File.ReadAllTextAsync(
                                toyPath,
                                TestContext.Current.CancellationToken
                            ),
                            ShaderSourceLanguage.ShadertoyGlsl,
                            "mainImage",
                            8,
                            8,
                            1
                        )
            ],
                    new Dictionary<string, uint> { ["iChannelHeat"] = 1 },
                    GpuPixelFormat.R16G16B16A16Float,
                    new Dictionary<string, ShaderConfigField> {
                ["gain"] = new(ShaderValueType.Float3),
                ["bias"] = new(ShaderValueType.Float),
            }
                ),
                TestContext.Current.CancellationToken
            );

            Assert.True(
                condition: toy.IsSuccess,
                userMessage: string.Join(
                    separator: Environment.NewLine,
                    values: toy.Diagnostics.Select(selector: static d => d.Message)
                )
            );
            Assert.NotEmpty(collection: toy.Spirv.ToArray());
            Assert.NotEmpty(collection: toy.Dxil.ToArray());
        } finally {
            try { Directory.Delete(
                root,
                recursive: true
            ); } catch (IOException) { }
        }
    }
}
