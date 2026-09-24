namespace Puck.Shaders.Tests;

/// <summary>Exercises the compiler against the installed DXC.</summary>
public sealed class ShaderCompilerNativeToolTests {
    [Fact]
    public async Task Dxc_compiles_hlsl_compute_and_graphics_for_both_backends() {
        var toolchain = new ShaderToolchain();

        Assert.SkipWhen(
            condition: (toolchain.Locate(name: ShaderCompiler.DxcTool) is null),
            reason: "DXC is required for this native compiler test."
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

            var compiler = new ShaderCompiler(Path.Combine(
                path1: root,
                path2: "cache"
            ));
            var compute = await compiler.CompileAsync(
                new ShaderCompilationRequest(
                    name: "native-compute",
                    stages: [
                new ShaderStageSource(
                            ShaderStage.Compute,
                            computePath,
                            await File.ReadAllTextAsync(
                                computePath,
                                TestContext.Current.CancellationToken
                            ),
                            "customEntry"
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
                    name: "native-graphics",
                    stages: [
                new ShaderStageSource(
                            ShaderStage.Vertex,
                            vertexPath,
                            await File.ReadAllTextAsync(
                                vertexPath,
                                TestContext.Current.CancellationToken
                            ),
                            "main"
                        ),
                new ShaderStageSource(
                            ShaderStage.Fragment,
                            fragmentPath,
                            await File.ReadAllTextAsync(
                                fragmentPath,
                                TestContext.Current.CancellationToken
                            ),
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
        } finally {
            try {
                Directory.Delete(
                root,
                recursive: true
            );
            } catch (IOException) { }
        }
    }
}
