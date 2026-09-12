using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Shaders.Tests;

public sealed class ShaderCompilerAdapterTests
{
    [Fact]
    public void Shadertoy_channels_are_bound_by_descriptor_without_blanket_rejection()
    {
        const string source = "void mainImage(out vec4 color, in vec2 coord) { color = texture(iChannel0, coord); }";
        var adapted = ShadertoyShaderAdapter.Adapt(source, new Dictionary<string, uint> { ["iChannel0"] = 7 });

        Assert.Contains("binding = 7", adapted.Text);
        Assert.DoesNotContain("rgba16f", adapted.Text);
        Assert.Contains("sampler2D iChannel0", adapted.Text);
        Assert.Contains("mainImage(puckColor, fragCoord)", adapted.Text);
        Assert.Single(adapted.Channels);
        Assert.Equal(7u, adapted.Channels[0].Binding);
    }

    [Fact]
    public void Explicit_channel_map_refuses_an_undeclared_channel()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            ShadertoyShaderAdapter.Adapt(
                "void mainImage(out vec4 c, in vec2 p) { c = texture(iChannel0, p); }",
                new Dictionary<string, uint>()));
        Assert.Contains("iChannel0", ex.Message);
    }
    [Fact]
    public void Adapter_uses_authored_workgroup_dimensions()
    {
        var adapted = ShadertoyShaderAdapter.Adapt("void mainImage(out vec4 c, in vec2 p) { c = vec4(1); }", groupSizeX: 16, groupSizeY: 4, groupSizeZ: 2);

        Assert.Contains("local_size_x = 16, local_size_y = 4, local_size_z = 2", adapted.Text);
        var floatOutput = ShadertoyShaderAdapter.Adapt("void mainImage(out vec4 c, in vec2 p) { c = vec4(1); }", outputFormat: Puck.Abstractions.Gpu.GpuPixelFormat.R32G32B32A32Float);
        Assert.Contains("rgba32f", floatOutput.Text);
    }

    [Fact]
    public void Config_fields_use_shared_hlsl_offsets_and_glsl_types()
    {
        var config = new Dictionary<string, ShaderConfigField>
        {
            ["gain"] = new(ShaderValueType.Float2),
            ["count"] = new(ShaderValueType.Uint)
        };
        var adapted = ShadertoyShaderAdapter.Adapt(
            "void mainImage(out vec4 c, in vec2 p) { c = vec4(gain, float(count), 1); }",
            config: config);
        Assert.Contains("layout(offset = 112) vec2 gain;", adapted.Text);
        Assert.Contains("layout(offset = 120) uint count;", adapted.Text);
        Assert.Contains("#define gain puck.gain", adapted.Text);
    }
    [Fact]
    public void Frame_constants_keep_the_cross_backend_wire_size()
    {
        Assert.Equal(ShaderFrameConstants.SizeBytes, Marshal.SizeOf<ShaderFrameConstants>());
        var constants = new ShaderFrameConstants(new Vector3(1, 2, 3), 4, 5, 6, default, default, default, default, 7, default, 8, default, 9);
        var bytes = new byte[ShaderFrameConstants.SizeBytes];
        constants.CopyTo(bytes);
        Assert.Equal(112, bytes.Length);
    }
}
