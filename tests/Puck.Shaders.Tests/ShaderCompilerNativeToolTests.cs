using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>Exercises the compiler against the installed DXC/glslang/spirv-cross toolchain.</summary>
public sealed class ShaderCompilerNativeToolTests
{
    [Fact]
    public async Task Native_tools_compile_hlsl_graphics_and_shadertoy_channels_for_both_backends()
    {
        var dxc = Find("dxc");
        var glslang = Find("glslangValidator", "glslang");
        var cross = Find("spirv-cross");
        Assert.SkipWhen(dxc is null || glslang is null || cross is null,
            "DXC, glslangValidator, and spirv-cross are required for this native compiler test.");

        var root = Path.Combine(Path.GetTempPath(), "puck-shader-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var vertexPath = Path.Combine(root, "native.vert.hlsl");
            var fragmentPath = Path.Combine(root, "native.frag.hlsl");
            var toyPath = Path.Combine(root, "native.glsl");
            await File.WriteAllTextAsync(vertexPath, "struct Out { float4 position : SV_Position; }; Out main(uint id : SV_VertexID) { Out o; o.position = float4((id == 2 ? 3.0 : -1.0), (id == 1 ? 3.0 : -1.0), 0.0, 1.0); return o; }", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(fragmentPath, "float4 main() : SV_Target { return float4(0.25, 0.5, 0.75, 1.0); }", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(toyPath, "void mainImage(out vec4 color, in vec2 fragCoord) { color = texture(iChannelHeat, fragCoord / iResolution.xy); }", TestContext.Current.CancellationToken);

            var compiler = new ShaderCompiler(Path.Combine(root, "cache"));
            var graphics = await compiler.CompileAsync(new ShaderCompilationRequest("native-graphics", [
                new ShaderStageSource(ShaderStage.Vertex, vertexPath, await File.ReadAllTextAsync(vertexPath, TestContext.Current.CancellationToken), ShaderSourceLanguage.Hlsl, "main"),
                new ShaderStageSource(ShaderStage.Fragment, fragmentPath, await File.ReadAllTextAsync(fragmentPath, TestContext.Current.CancellationToken), ShaderSourceLanguage.Hlsl, "main")
            ]), TestContext.Current.CancellationToken);
            Assert.True(graphics.IsSuccess, string.Join(Environment.NewLine, graphics.Diagnostics.Select(static d => d.Message)));
            Assert.Equal(2, graphics.SpirvByStage.Count);
            Assert.Equal(2, graphics.DxilByStage.Count);
            Assert.NotEmpty(graphics.SpirvByStage[ShaderStage.Vertex].ToArray());
            Assert.NotEmpty(graphics.DxilByStage[ShaderStage.Fragment].ToArray());

            var toy = await compiler.CompileAsync(new ShaderCompilationRequest("native-toy", [
                new ShaderStageSource(ShaderStage.Compute, toyPath, await File.ReadAllTextAsync(toyPath, TestContext.Current.CancellationToken), ShaderSourceLanguage.ShadertoyGlsl, "mainImage", 8, 8, 1)
            ], new Dictionary<string, uint> { ["iChannelHeat"] = 1 }, GpuPixelFormat.R16G16B16A16Float), TestContext.Current.CancellationToken);
            Assert.True(toy.IsSuccess, string.Join(Environment.NewLine, toy.Diagnostics.Select(static d => d.Message)));
            Assert.NotEmpty(toy.Spirv.ToArray());
            Assert.NotEmpty(toy.Dxil.ToArray());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static string? Find(params string[] names)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) { return null; }
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) { return candidate; }
                if (File.Exists(candidate + ".exe")) { return candidate + ".exe"; }
            }
        }
        return null;
    }
}