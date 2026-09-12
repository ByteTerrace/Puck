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
    public void Comments_do_not_create_channel_bindings()
    {
        var adapted = ShadertoyShaderAdapter.Adapt("// iChannel0 is documentation\nvoid mainImage(out vec4 c, in vec2 p) { c = vec4(1); }");
        Assert.Empty(adapted.Channels);
        Assert.DoesNotContain("sampler2D iChannel0", adapted.Text);
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
        Assert.Contains("layout(offset = 112) uint count;", adapted.Text);
        Assert.Contains("layout(offset = 116) float puck_gain_0;", adapted.Text);
        Assert.Contains("layout(offset = 120) float puck_gain_1;", adapted.Text);
        Assert.Contains("#define gain vec2(puck.puck_gain_0, puck.puck_gain_1)", adapted.Text);
    }
    [Fact]
    public void Config_fields_use_canonical_ordinal_order_for_vector_packing()
    {
        var config = new Dictionary<string, ShaderConfigField>
        {
            ["zVector"] = new(ShaderValueType.Float2),
            ["aScalar"] = new(ShaderValueType.Float),
            ["mVector"] = new(ShaderValueType.Float3),
        };

        var adapted = ShadertoyShaderAdapter.Adapt(
            "void mainImage(out vec4 c, in vec2 p) { c = vec4(zVector, aScalar, mVector.x); }",
            config: config);

        Assert.Contains("layout(offset = 112) float aScalar;", adapted.Text);
        Assert.Contains("layout(offset = 116) float puck_mVector_0;", adapted.Text);
        Assert.Contains("layout(offset = 120) float puck_mVector_1;", adapted.Text);
        Assert.Contains("layout(offset = 124) float puck_mVector_2;", adapted.Text);
        Assert.Contains("layout(offset = 128) float puck_zVector_0;", adapted.Text);
        Assert.Contains("layout(offset = 132) float puck_zVector_1;", adapted.Text);
        Assert.True(adapted.Text.IndexOf("float aScalar", StringComparison.Ordinal) < adapted.Text.IndexOf("float puck_mVector_0", StringComparison.Ordinal));
        Assert.True(adapted.Text.IndexOf("float puck_mVector_0", StringComparison.Ordinal) < adapted.Text.IndexOf("float puck_zVector_0", StringComparison.Ordinal));
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
