using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Shaders;

/// <summary>One user-authored pipeline parameter packed after <see cref="ShaderFrameConstants"/>.</summary>
public sealed record ShaderPipelineParameterSlot(
    string Name,
    ShaderValueType Type,
    uint Offset
);

/// <summary>Resolved offsets and shared schema for one pass's live parameters.</summary>
public sealed class ShaderPipelineParameterLayout {
    /// <summary>The byte offset at which authored parameters begin.</summary>
    public const uint FramePrefixBytes = ShaderFrameConstants.SizeBytes;

    private ShaderPipelineParameterLayout(
        IReadOnlyList<ShaderPipelineParameterSlot> slots,
        IReadOnlyDictionary<string, ShaderConfigField>? schema,
        uint sizeBytes
    ) {
        Slots = new ReadOnlyCollection<ShaderPipelineParameterSlot>(slots.ToList());
        Schema = schema is null ? null : new ReadOnlyDictionary<string, ShaderConfigField>(
            SnapshotConfig(schema));
        SizeBytes = sizeBytes;
    }

    /// <summary>Gets parameters in the canonical ordinal name order used by shader generation.</summary>
    public IReadOnlyList<ShaderPipelineParameterSlot> Slots { get; }
    /// <summary>Gets the shared config schema, or <see langword="null"/> for no parameters.</summary>
    public IReadOnlyDictionary<string, ShaderConfigField>? Schema { get; }
    /// <summary>Gets the full block size including the frame prefix.</summary>
    public uint SizeBytes { get; }

    private static Dictionary<string, ShaderConfigField> SnapshotConfig(IReadOnlyDictionary<string, ShaderConfigField> schema) =>
        schema.ToDictionary(static pair => pair.Key, static pair => pair.Value with {
            Default = pair.Value.Default is { } value ? value.Clone() : null,
        }, StringComparer.Ordinal);
    /// <summary>Resolves parameter offsets using the same packing rule as shader-set push constants.</summary>
    public static ShaderPipelineParameterLayout Resolve(ShaderPipelinePass pass) {
        ArgumentNullException.ThrowIfNull(argument: pass);
        ShaderConfigBinding.ValidateSchema(schema: pass.Config, ownerName: pass.Name);

        if ((pass.Config is null) || (pass.Config.Count == 0)) {
            return new ShaderPipelineParameterLayout(slots: [], schema: pass.Config, sizeBytes: FramePrefixBytes);
        }

        // Shader adapters generate declarations in ordinal name order. Keep layout generation
        // on that same order so offsets remain stable when JSON/object insertion order changes.
        var fields = pass.Config.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToArray();
        var types = fields.Select(static pair => pair.Value.Type).ToArray();
        var offsets = ShaderPushConstantLayout.ComputeOffsets(types: types, sizeBytes: out var configBytes);
        var slots = fields.Select((pair, index) => new ShaderPipelineParameterSlot(
            Name: pair.Key,
            Type: pair.Value.Type,
            Offset: FramePrefixBytes + offsets[index])).ToArray();

        return new ShaderPipelineParameterLayout(slots: slots, schema: pass.Config, sizeBytes: FramePrefixBytes + configBytes);
    }

    /// <summary>Emits the config JSON Schema through the shared binder.</summary>
    public JsonObject JsonSchema(string? description = null) =>
        ShaderConfigBinding.JsonSchema(schema: Schema, description: description);

    /// <summary>Binds authored values and returns a complete frame-plus-parameter byte block.</summary>
    public bool TryBind(JsonElement? config, out ShaderPipelineParameterValues values, out string reason) {
        values = new ShaderPipelineParameterValues(
            Config: ShaderConfigValues.Empty,
            Bytes: new byte[SizeBytes]);

        if (!ShaderConfigBinding.TryBind(schema: Schema, config: config, ownerName: "pipeline pass", values: out var bound, reason: out reason)) {
            return false;
        }

        var bytes = new byte[SizeBytes];

        foreach (var slot in Slots) {
            bound[slot.Name].Bytes.Span.CopyTo(bytes.AsSpan((int)slot.Offset));
        }

        values = new ShaderPipelineParameterValues(Config: bound, Bytes: bytes);
        return true;
    }
}

/// <summary>Bound config values and bytes ready to append after the frame constants.</summary>
public sealed record ShaderPipelineParameterValues(
    ShaderConfigValues Config,
    ReadOnlyMemory<byte> Bytes
);
