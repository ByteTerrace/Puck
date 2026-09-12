using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>Wraps Shadertoy GLSL into Puck's cross-backend compute ABI.</summary>
public static partial class ShadertoyShaderAdapter
{
    public const string Version = "puck-shadertoy-3";
    public const uint DefaultWorkgroupSize = 8;

    /// <summary>One adapted source and the line offset used for diagnostics.</summary>
    public sealed record AdaptedSource(string Text, int PrefixLineCount, IReadOnlyList<ShaderChannelBinding> Channels);

    /// <summary>Adapts a Shadertoy fragment into a compute shader with explicit output, channel, frame, and parameter bindings.</summary>
    public static AdaptedSource Adapt(
        string source,
        IReadOnlyDictionary<string, uint>? channelBindings = null,
        uint groupSizeX = DefaultWorkgroupSize,
        uint groupSizeY = DefaultWorkgroupSize,
        uint groupSizeZ = 1,
        GpuPixelFormat outputFormat = GpuPixelFormat.R8G8B8A8Unorm,
        IReadOnlyDictionary<string, ShaderConfigField>? config = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (groupSizeX == 0 || groupSizeY == 0 || groupSizeZ == 0) { throw new ArgumentOutOfRangeException(nameof(groupSizeX)); }

        var names = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        var undeclared = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in ChannelReferencePattern().Matches(source))
        {
            var name = match.Value;
            if (channelBindings is not null)
            {
                if (channelBindings.TryGetValue(name, out var mapped)) { names[name] = mapped; }
                else { undeclared.Add(name); }
            }
            else if (NumericChannelPattern().Match(name) is { Success: true } numeric)
            {
                names[name] = uint.Parse(numeric.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 1;
            }
        }
        if (undeclared.Count != 0)
        {
            throw new InvalidDataException("Shadertoy source references channel(s) not declared by the pipeline: " + string.Join(", ", undeclared.Order(StringComparer.Ordinal)) + ".");
        }
        if (channelBindings is not null)
        {
            foreach (var pair in channelBindings) { names.TryAdd(pair.Key, pair.Value); }
        }
        var configBlock = ConfigDeclarations(config);
        var prelude = string.Join("\n", [
            "#version 450", "", "#define PUCK_SHADERTOY 1",
            $"layout(local_size_x = {groupSizeX}, local_size_y = {groupSizeY}, local_size_z = {groupSizeZ}) in;",
            $"layout(set = 0, binding = 0, {ImageFormatQualifier(outputFormat)}) uniform writeonly image2D puckShaderImage;",
            "layout(push_constant, std430) uniform PuckShaderFrame {",
            "    vec3 iResolution;", "    float iTime;", "    float iTimeDelta;", "    int iFrame;", "    vec2 _pad0;",
            "    vec4 iMouse;", "    vec4 iDate;", "    vec3 iCameraPos;", "    float iCameraFov;",
            "    vec3 iCameraTarget;", "    float _pad1;", "    vec3 iCameraUp;", "    float _pad2;",
            configBlock.Declarations,
            "} puck;",
            configBlock.Macros,
            "#define iResolution puck.iResolution", "#define iTime puck.iTime", "#define iTimeDelta puck.iTimeDelta",
            "#define iFrame puck.iFrame", "#define iMouse puck.iMouse", "#define iDate puck.iDate",
            "#define iCameraPos puck.iCameraPos", "#define iCameraFov puck.iCameraFov",
            "#define iCameraTarget puck.iCameraTarget", "#define iCameraUp puck.iCameraUp", ""]);
        var builder = new StringBuilder(prelude);
        foreach (var pair in names)
        {
            if (!SamplerDeclarationPattern().IsMatch(source) || !source.Contains($" {pair.Key}", StringComparison.Ordinal))
            {
                builder.Append("layout(set = 0, binding = ").Append(pair.Value)
                    .Append(") uniform sampler2D ").Append(pair.Key).AppendLine(";");
            }
        }
        builder.AppendLine();
        builder.AppendLine(source);
        builder.AppendLine();
        builder.AppendLine("void main() {");
        builder.AppendLine("    ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);");
        builder.AppendLine("    if (pixel.x >= int(iResolution.x) || pixel.y >= int(iResolution.y)) return;");
        builder.AppendLine("    vec2 fragCoord = vec2(float(pixel.x) + 0.5, iResolution.y - (float(pixel.y) + 0.5));");
        builder.AppendLine("    vec4 puckColor = vec4(0.0);");
        builder.AppendLine("    mainImage(puckColor, fragCoord);");
        builder.AppendLine("    imageStore(puckShaderImage, pixel, puckColor);");
        builder.AppendLine("}");

        var channels = names.Select(static pair => new ShaderChannelBinding(pair.Key, pair.Value)).ToArray();
        return new AdaptedSource(builder.ToString(), prelude.Count(static c => c == '\n') + names.Count,
            new ReadOnlyCollection<ShaderChannelBinding>(channels));
    }

    public static AdaptedSource Adapt(string source, ShaderCompilationRequest descriptor) =>
        Adapt(source, descriptor.Channels, outputFormat: descriptor.OutputFormat, config: descriptor.Config);

    private static (string Declarations, string Macros) ConfigDeclarations(IReadOnlyDictionary<string, ShaderConfigField>? config)
    {
        if (config is null || config.Count == 0) { return (string.Empty, string.Empty); }
        var fields = config.ToArray();
        var offsets = ShaderPushConstantLayout.ComputeOffsets(fields.Select(static pair => pair.Value.Type).ToArray(), out _);
        var declarations = new StringBuilder();
        var macros = new StringBuilder();
        for (var index = 0; index < fields.Length; index++)
        {
            var (name, field) = fields[index];
            var offset = ShaderPipelineParameterLayout.FramePrefixBytes + offsets[index];
            declarations.Append("    layout(offset = ").Append(offset).Append(") ")
                .Append(GlslType(field.Type)).Append(' ').Append(name).AppendLine(";");
            macros.Append("#define ").Append(name).Append(" puck.").AppendLine(name);
        }
        return (declarations.ToString(), macros.ToString());
    }

    private static string GlslType(ShaderValueType type) => type switch
    {
        ShaderValueType.Float => "float", ShaderValueType.Float2 => "vec2", ShaderValueType.Float3 => "vec3", ShaderValueType.Float4 => "vec4",
        ShaderValueType.Uint => "uint", ShaderValueType.Uint2 => "uvec2", ShaderValueType.Uint3 => "uvec3", ShaderValueType.Uint4 => "uvec4",
        ShaderValueType.Int => "int", ShaderValueType.Int2 => "ivec2", ShaderValueType.Int3 => "ivec3", ShaderValueType.Int4 => "ivec4",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The value type is not defined.")
    };

    private static string ImageFormatQualifier(GpuPixelFormat format) => format switch {
        GpuPixelFormat.R8G8B8A8Unorm => "rgba8", GpuPixelFormat.R16G16B16A16Float => "rgba16f", GpuPixelFormat.R32G32B32A32Float => "rgba32f",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Shadertoy output requires an RGBA image format.")
    };

    [GeneratedRegex(@"\biChannel(?:[A-Za-z_][A-Za-z0-9_]*|[0-9]+)\b")]
    private static partial Regex ChannelReferencePattern();
    [GeneratedRegex(@"^iChannel(\d+)$")]
    private static partial Regex NumericChannelPattern();
    [GeneratedRegex(@"\bsampler2D\s+iChannel[A-Za-z_][A-Za-z0-9_]*\b")]
    private static partial Regex SamplerDeclarationPattern();
}