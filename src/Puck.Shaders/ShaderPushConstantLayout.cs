using System.Text.Json;

using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>Where one resolved push-constant slot's value comes from.</summary>
public enum ShaderPushConstantSourceKind {
    /// <summary>A bound config value, copied once.</summary>
    Config = 0,
    /// <summary>The fixed-step simulation clock (<c>FrameContext.ElapsedTicks</c>), optionally quantized.</summary>
    Tick = 1,
    /// <summary>The pass's width and height in pixels.</summary>
    Resolution = 2,
    /// <summary>The pass's own produced-frame counter.</summary>
    Frame = 3,
}
/// <summary>One resolved push-constant field: its byte offset and the parsed source it is filled from.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="Type">The field's value type.</param>
/// <param name="Offset">The field's byte offset inside the block.</param>
/// <param name="Kind">The source kind.</param>
/// <param name="ConfigField">For <see cref="ShaderPushConstantSourceKind.Config"/>: the config field's name.</param>
/// <param name="QuantizeHzLiteral">For a quantized tick: the authored literal rate, or <see langword="null"/>.</param>
/// <param name="QuantizeHzConfigField">For a quantized tick: the <c>uint</c> config field naming the rate, or
/// <see langword="null"/>.</param>
public sealed record ShaderPushConstantSlot(
    string Name,
    ShaderValueType Type,
    uint Offset,
    ShaderPushConstantSourceKind Kind,
    string? ConfigField,
    uint? QuantizeHzLiteral,
    string? QuantizeHzConfigField
);
/// <summary>
/// A <see cref="ShaderSetManifest"/>'s push-constant block resolved to byte offsets and parsed sources. The
/// packing rule is HLSL constant-buffer packing, which is what DXC emits for a <c>ConstantBuffer&lt;T&gt;</c> push
/// constant on both targets — Direct3D 12 root constants and, under DXC's default SPIR-V layout, Vulkan push
/// constants: fields are laid out in declaration order, every field starts on a 4-byte boundary, and a vector
/// that would straddle a 16-byte boundary is bumped to the next 16-byte row. There is no other padding, and the
/// block's size is the end of its last field.
/// </summary>
public sealed class ShaderPushConstantLayout {
    private const string ConfigSourcePrefix = "config.";
    private const uint RowBytes = 16;

    private ShaderPushConstantLayout(IReadOnlyList<ShaderPushConstantSlot> slots, uint sizeBytes, GpuShaderStage stages) {
        Slots = slots;
        SizeBytes = sizeBytes;
        Stages = stages;
    }

    /// <summary>Gets the block's byte size (the end of its last field; a multiple of 4).</summary>
    public uint SizeBytes { get; }
    /// <summary>Gets the resolved slots, in declaration order.</summary>
    public IReadOnlyList<ShaderPushConstantSlot> Slots { get; }
    /// <summary>Gets the stages that read the block.</summary>
    public GpuShaderStage Stages { get; }

    /// <summary>Computes each field's byte offset under the packing rule, for a field list given only by type.</summary>
    /// <param name="types">The field types, in declaration order.</param>
    /// <param name="sizeBytes">The block's byte size.</param>
    /// <returns>One byte offset per field.</returns>
    public static uint[] ComputeOffsets(IReadOnlyList<ShaderValueType> types, out uint sizeBytes) {
        ArgumentNullException.ThrowIfNull(argument: types);

        var offsets = new uint[types.Count];
        var end = 0u;

        for (var index = 0; (index < types.Count); index++) {
            var size = types[index].SizeBytes();
            var offset = end;

            if (((offset % RowBytes) + size) > RowBytes) {
                offset = ((((offset + RowBytes) - 1) / RowBytes) * RowBytes);
            }

            offsets[index] = offset;
            end = (offset + size);
        }

        sizeBytes = end;

        return offsets;
    }
    /// <summary>Resolves a manifest's push-constant block against its config schema: offsets under the packing rule,
    /// each source parsed and type-checked, each quantization rate checked against the engine tick base.</summary>
    /// <param name="block">The authored block.</param>
    /// <param name="config">The manifest's config schema (name → field), or <see langword="null"/> when it has none.</param>
    /// <param name="manifestName">The manifest's name, for refusal messages.</param>
    /// <returns>The resolved layout.</returns>
    /// <exception cref="InvalidDataException">A field name repeats, a stage or source is unrecognized, a source's
    /// type does not fit the field's type, a <c>config.</c> reference names no config field, or a quantization rate
    /// is neither a positive literal dividing <see cref="EngineTicks.PerSecond"/> nor a <c>uint</c> config reference.</exception>
    public static ShaderPushConstantLayout Resolve(ShaderPushConstantBlock block, IReadOnlyDictionary<string, ShaderConfigField>? config, string manifestName) {
        ArgumentNullException.ThrowIfNull(argument: block);

        var stages = GpuShaderStage.None;

        foreach (var stage in block.Stages) {
            stages |= stage switch {
                "vertex" => GpuShaderStage.Vertex,
                "fragment" => GpuShaderStage.Fragment,
                "compute" => GpuShaderStage.Compute,
                _ => throw new InvalidDataException(message: $"'{manifestName}' pushConstants.stages names '{stage}'; expected vertex, fragment, or compute."),
            };
        }

        if (stages == GpuShaderStage.None) {
            throw new InvalidDataException(message: $"'{manifestName}' pushConstants.stages must name at least one stage.");
        }

        var types = new ShaderValueType[block.Fields.Count];

        for (var index = 0; (index < types.Length); index++) {
            types[index] = block.Fields[index].Type;
        }

        var offsets = ComputeOffsets(sizeBytes: out var sizeBytes, types: types);
        var slots = new ShaderPushConstantSlot[block.Fields.Count];
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < slots.Length); index++) {
            var field = block.Fields[index];

            if (string.IsNullOrEmpty(value: field.Name) || !names.Add(item: field.Name)) {
                throw new InvalidDataException(message: $"'{manifestName}' pushConstants field #{index} has an empty or repeated name '{field.Name}'.");
            }

            slots[index] = ResolveSlot(config: config, field: field, manifestName: manifestName, offset: offsets[index]);
        }

        return new ShaderPushConstantLayout(sizeBytes: sizeBytes, slots: slots, stages: stages);
    }

    private static ShaderPushConstantSlot ResolveSlot(IReadOnlyDictionary<string, ShaderConfigField>? config, ShaderPushConstantField field, string manifestName, uint offset) {
        var source = field.Source;

        if (source.StartsWith(comparisonType: StringComparison.Ordinal, value: ConfigSourcePrefix)) {
            var configName = source[ConfigSourcePrefix.Length..];
            var configField = FindConfigField(config: config, name: configName, manifestName: manifestName, what: $"push-constant field '{field.Name}'");

            if (configField.Type != field.Type) {
                throw new InvalidDataException(message: $"'{manifestName}' push-constant field '{field.Name}' is {field.Type.Spelling()} but its source config field '{configName}' is {configField.Type.Spelling()}.");
            }
            if (field.QuantizeHz is not null) {
                throw new InvalidDataException(message: $"'{manifestName}' push-constant field '{field.Name}' carries quantizeHz, which only a tick source accepts.");
            }

            return new ShaderPushConstantSlot(Name: field.Name, Type: field.Type, Offset: offset, Kind: ShaderPushConstantSourceKind.Config, ConfigField: configName, QuantizeHzLiteral: null, QuantizeHzConfigField: null);
        }

        switch (source) {
            case "tick": {
                    if ((field.Type != ShaderValueType.Uint) && (field.Type != ShaderValueType.Uint2)) {
                        throw new InvalidDataException(message: $"'{manifestName}' push-constant field '{field.Name}' sources tick, which must be uint (low 32 bits) or uint2 (low, high); it is {field.Type.Spelling()}.");
                    }

                    var (literal, configName) = ResolveQuantization(config: config, field: field, manifestName: manifestName);

                    return new ShaderPushConstantSlot(Name: field.Name, Type: field.Type, Offset: offset, Kind: ShaderPushConstantSourceKind.Tick, ConfigField: null, QuantizeHzLiteral: literal, QuantizeHzConfigField: configName);
                }
            case "resolution":
                RefuseQuantization(field: field, manifestName: manifestName);

                if ((field.Type != ShaderValueType.Float2) && (field.Type != ShaderValueType.Uint2)) {
                    throw new InvalidDataException(message: $"'{manifestName}' push-constant field '{field.Name}' sources resolution, which must be float2 or uint2; it is {field.Type.Spelling()}.");
                }

                return new ShaderPushConstantSlot(Name: field.Name, Type: field.Type, Offset: offset, Kind: ShaderPushConstantSourceKind.Resolution, ConfigField: null, QuantizeHzLiteral: null, QuantizeHzConfigField: null);
            case "frame":
                RefuseQuantization(field: field, manifestName: manifestName);

                if (field.Type != ShaderValueType.Uint) {
                    throw new InvalidDataException(message: $"'{manifestName}' push-constant field '{field.Name}' sources frame, which must be uint; it is {field.Type.Spelling()}.");
                }

                return new ShaderPushConstantSlot(Name: field.Name, Type: field.Type, Offset: offset, Kind: ShaderPushConstantSourceKind.Frame, ConfigField: null, QuantizeHzLiteral: null, QuantizeHzConfigField: null);
            default:
                throw new InvalidDataException(message: $"'{manifestName}' push-constant field '{field.Name}' has source '{source}'; expected config.<field>, tick, resolution, or frame.");
        }
    }
    private static (uint? Literal, string? ConfigField) ResolveQuantization(IReadOnlyDictionary<string, ShaderConfigField>? config, ShaderPushConstantField field, string manifestName) {
        if (field.QuantizeHz is not { } quantize) {
            return (null, null);
        }

        if (quantize.ValueKind == JsonValueKind.Number) {
            if (!quantize.TryGetUInt32(value: out var hz) || !DividesTickRate(hz: hz)) {
                throw new InvalidDataException(message: $"'{manifestName}' push-constant field '{field.Name}' quantizeHz must be a positive integer that divides {EngineTicks.PerSecond} exactly; it is {quantize.GetRawText()}.");
            }

            return (hz, null);
        }

        if ((quantize.ValueKind == JsonValueKind.String) && (quantize.GetString() is { } text) && text.StartsWith(comparisonType: StringComparison.Ordinal, value: ConfigSourcePrefix)) {
            var configName = text[ConfigSourcePrefix.Length..];
            var configField = FindConfigField(config: config, name: configName, manifestName: manifestName, what: $"push-constant field '{field.Name}' quantizeHz");

            if (configField.Type != ShaderValueType.Uint) {
                throw new InvalidDataException(message: $"'{manifestName}' push-constant field '{field.Name}' quantizeHz names config field '{configName}', which must be uint; it is {configField.Type.Spelling()}.");
            }

            return (null, configName);
        }

        throw new InvalidDataException(message: $"'{manifestName}' push-constant field '{field.Name}' quantizeHz must be a positive integer or a config.<field> reference.");
    }
    private static void RefuseQuantization(ShaderPushConstantField field, string manifestName) {
        if (field.QuantizeHz is not null) {
            throw new InvalidDataException(message: $"'{manifestName}' push-constant field '{field.Name}' carries quantizeHz, which only a tick source accepts.");
        }
    }
    private static ShaderConfigField FindConfigField(IReadOnlyDictionary<string, ShaderConfigField>? config, string name, string manifestName, string what) {
        if ((config is not null) && config.TryGetValue(key: name, value: out var field)) {
            return field;
        }

        throw new InvalidDataException(message: $"'{manifestName}' {what} references config field '{name}', which the manifest's config schema does not declare.");
    }

    /// <summary>Determines whether a rate divides the engine tick base exactly.</summary>
    /// <param name="hz">The rate in hertz.</param>
    /// <returns><see langword="true"/> when positive and dividing <see cref="EngineTicks.PerSecond"/>.</returns>
    public static bool DividesTickRate(uint hz) => ((hz != 0) && ((EngineTicks.PerSecond % hz) == 0));
}
