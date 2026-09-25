using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>One config field of a pass's frame block, at the byte offset its interface places it.</summary>
/// <param name="Name">The field's name.</param>
/// <param name="Type">The field's value type.</param>
/// <param name="Offset">The field's byte offset inside the frame block.</param>
public sealed record ShaderPipelineParameterSlot(
    string Name,
    ShaderValueType Type,
    uint Offset
);
/// <summary>
/// A pass's frame block: its <see cref="ShaderFrameInterface"/> interface, where the interface's layout places every
/// member, and the config schema its config fields bind through. <see cref="WriteFrame"/> is the host writer: it places
/// each frame member at the offset the layout gives it, which is the offset the generated declarations read it from.
/// </summary>
public sealed class ShaderPipelineParameterLayout {
    private readonly uint m_cameraFov;
    private readonly uint m_cameraPosition;
    private readonly uint m_cameraTarget;
    private readonly uint m_cameraUp;
    private readonly uint m_extent;
    private readonly uint m_frame;
    private readonly uint m_pointer;
    private readonly uint m_pointerDown;
    private readonly uint m_pointerPresses;
    private readonly uint m_tick;
    private readonly uint m_tickRate;
    private readonly uint m_time;
    private readonly uint m_timeDelta;

    private ShaderPipelineParameterLayout(ShaderInterface shaderInterface, IReadOnlyDictionary<string, ShaderConfigField>? schema) {
        var layout = shaderInterface.Layout();
        var block = layout.PushedGroup!;
        var offsets = block.BlockMembers.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static member => member.Offset,
            keySelector: static member => member.Name
        );

        Interface = shaderInterface;
        Layout = layout;
        SizeBytes = block.BlockSizeBytes;
        Schema = ((schema is null)
            ? null
            : new ReadOnlyDictionary<string, ShaderConfigField>(dictionary: SnapshotConfig(schema: schema)));
        Slots = new ReadOnlyCollection<ShaderPipelineParameterSlot>(list: ((schema is null)
            ? []
            : schema.Keys.Order(comparer: StringComparer.Ordinal).Select(selector: name => new ShaderPipelineParameterSlot(
                Name: name,
                Offset: offsets[name],
                Type: schema[name].Type
            )).ToArray()));
        m_extent = offsets[ShaderFrameInterface.Extent];
        m_pointer = offsets[ShaderFrameInterface.Pointer];
        m_tick = offsets[ShaderFrameInterface.Tick];
        m_time = offsets[ShaderFrameInterface.Time];
        m_timeDelta = offsets[ShaderFrameInterface.TimeDelta];
        m_frame = offsets[ShaderFrameInterface.Frame];
        m_tickRate = offsets[ShaderFrameInterface.TickRate];
        m_pointerDown = offsets[ShaderFrameInterface.PointerDown];
        m_pointerPresses = offsets[ShaderFrameInterface.PointerPresses];
        m_cameraPosition = offsets[ShaderFrameInterface.CameraPosition];
        m_cameraFov = offsets[ShaderFrameInterface.CameraFov];
        m_cameraTarget = offsets[ShaderFrameInterface.CameraTarget];
        m_cameraUp = offsets[ShaderFrameInterface.CameraUp];
    }

    /// <summary>Gets the pass's interface.</summary>
    public ShaderInterface Interface { get; }
    /// <summary>Gets where the interface places every member.</summary>
    public ShaderInterfaceLayout Layout { get; }
    /// <summary>Gets the shared config schema, or <see langword="null"/> for no parameters.</summary>
    public IReadOnlyDictionary<string, ShaderConfigField>? Schema { get; }
    /// <summary>Gets the frame block's size in bytes, a multiple of 16.</summary>
    public uint SizeBytes { get; }
    /// <summary>Gets the config fields in ordinal name order, each at its offset inside the frame block.</summary>
    public IReadOnlyList<ShaderPipelineParameterSlot> Slots { get; }

    private static Dictionary<string, ShaderConfigField> SnapshotConfig(IReadOnlyDictionary<string, ShaderConfigField> schema) =>
        schema.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value with {
                Default = ((pair.Value.Default is { } value)
            ? value.Clone()
            : null),
            },
            StringComparer.Ordinal
        );
    private static void WriteVector3(Span<byte> block, uint offset, Vector3 value) {
        BinaryPrimitives.WriteSingleLittleEndian(
            destination: block[((int)offset)..],
            value: value.X
        );
        BinaryPrimitives.WriteSingleLittleEndian(
            destination: block[((int)(offset + 4))..],
            value: value.Y
        );
        BinaryPrimitives.WriteSingleLittleEndian(
            destination: block[((int)(offset + 8))..],
            value: value.Z
        );
    }
    private static void WriteUInt32(Span<byte> block, uint offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: block[((int)offset)..],
            value: value
        );
    private static void WriteSingle(Span<byte> block, uint offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(
            destination: block[((int)offset)..],
            value: value
        );

    /// <summary>Emits the config JSON Schema through the shared binder.</summary>
    /// <param name="description">The schema's description, or <see langword="null"/>.</param>
    /// <returns>The schema.</returns>
    public JsonObject JsonSchema(string? description = null) =>
        ShaderConfigBinding.JsonSchema(
            schema: Schema,
            description: description
        );
    /// <summary>Resolves a pass's frame block from its declaration: the interface its source names
    /// (<see cref="ShaderFrameInterface.NameOf"/>), or a package pass's package id names, over the frame members and
    /// its config.</summary>
    /// <param name="pass">The pass.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="InvalidDataException">The config schema is invalid, the source's file does not name an
    /// interface, or a config field's name is not an identifier or repeats a frame member's.</exception>
    public static ShaderPipelineParameterLayout Resolve(ShaderPipelinePass pass) {
        ArgumentNullException.ThrowIfNull(argument: pass);
        ShaderConfigBinding.ValidateSchema(
            schema: pass.Config,
            ownerName: pass.Name
        );

        // A package pass reads no generated declarations; its interface is named for its package id, each character an
        // interface name cannot hold, such as the period of sdf.world, spelled as a hyphen.
        return For(
            config: pass.Config,
            interfaceName: ((pass.Kind == ShaderPipelinePassKind.Package)
                ? string.Concat(values: pass.Source.Select(selector: static character => ((char.IsAsciiLetterLower(c: character) || char.IsAsciiDigit(c: character))
                    ? character
                    : '-')))
                : ShaderFrameInterface.NameOf(sourcePath: pass.Source))
        );
    }
    /// <summary>Resolves the frame block of a named interface over a config schema.</summary>
    /// <param name="interfaceName">The interface's name.</param>
    /// <param name="config">The config schema, or <see langword="null"/> when there is none.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="InvalidDataException"><paramref name="interfaceName"/> is not an interface name, or a config
    /// field's name is not an identifier or repeats a frame member's.</exception>
    public static ShaderPipelineParameterLayout For(string interfaceName, IReadOnlyDictionary<string, ShaderConfigField>? config) =>
        new(
            schema: config,
            shaderInterface: ShaderFrameInterface.For(
                config: config,
                name: interfaceName
            )
        );
    /// <summary>Binds authored values and returns a complete frame block holding them at their offsets, with every
    /// frame member zero.</summary>
    /// <param name="config">The authored config object, or <see langword="null"/> for every default.</param>
    /// <param name="values">The bound values and the block.</param>
    /// <param name="reason">Why binding failed, or empty.</param>
    /// <returns><see langword="true"/> when every value binds.</returns>
    public bool TryBind(JsonElement? config, out ShaderPipelineParameterValues values, out string reason) {
        values = new ShaderPipelineParameterValues(
            Config: ShaderConfigValues.Empty,
            Bytes: new byte[SizeBytes]
        );

        if (!ShaderConfigBinding.TryBind(
            schema: Schema,
            config: config,
            ownerName: "pipeline pass",
            values: out var bound,
            reason: out reason
        )) {
            return false;
        }

        var bytes = new byte[SizeBytes];

        foreach (var slot in Slots) {
            bound[slot.Name].Bytes.Span.CopyTo(destination: bytes.AsSpan(start: ((int)slot.Offset)));
        }

        values = new ShaderPipelineParameterValues(
            Bytes: bytes,
            Config: bound
        );
        return true;
    }
    /// <summary>Writes every frame member into a frame block at the offset the interface places it, leaving the config
    /// fields and the padding as they are.</summary>
    /// <param name="block">The frame block, at least <see cref="SizeBytes"/> long.</param>
    /// <param name="values">The host's frame values.</param>
    /// <param name="width">The pass's output width, in pixels.</param>
    /// <param name="height">The pass's output height, in pixels.</param>
    /// <param name="tick">The engine tick the frame presents.</param>
    /// <param name="frame">The frames the pass's node submitted before this one; only the low 32 bits are written.</param>
    /// <exception cref="ArgumentException"><paramref name="block"/> is shorter than <see cref="SizeBytes"/>.</exception>
    public void WriteFrame(Span<byte> block, in ShaderFrameValues values, uint width, uint height, ulong tick, ulong frame) {
        if (block.Length < SizeBytes) {
            throw new ArgumentException(
                message: $"A frame block holds {SizeBytes} bytes; the destination holds {block.Length}.",
                paramName: nameof(block)
            );
        }

        WriteUInt32(block: block, offset: m_extent, value: width);
        WriteUInt32(block: block, offset: (m_extent + 4), value: height);
        WriteSingle(block: block, offset: m_pointer, value: values.Pointer.X);
        WriteSingle(block: block, offset: (m_pointer + 4), value: values.Pointer.Y);
        WriteUInt32(block: block, offset: m_tick, value: unchecked((uint)tick));
        WriteUInt32(block: block, offset: (m_tick + 4), value: ((uint)(tick >> 32)));
        WriteSingle(block: block, offset: m_time, value: ((float)values.Time));
        WriteSingle(block: block, offset: m_timeDelta, value: ((float)values.TimeDelta));
        WriteUInt32(block: block, offset: m_frame, value: unchecked((uint)frame));
        WriteUInt32(block: block, offset: m_tickRate, value: ((uint)EngineTicks.PerSecond));
        WriteUInt32(block: block, offset: m_pointerDown, value: (values.PointerDown ? 1u : 0u));
        WriteUInt32(block: block, offset: m_pointerPresses, value: values.PointerPresses);
        WriteVector3(block: block, offset: m_cameraPosition, value: values.CameraPosition);
        WriteSingle(block: block, offset: m_cameraFov, value: values.CameraFov);
        WriteVector3(block: block, offset: m_cameraTarget, value: values.CameraTarget);
        WriteVector3(block: block, offset: m_cameraUp, value: values.CameraUp);
    }
}
/// <summary>Bound config values, and a frame block holding them at their offsets.</summary>
/// <param name="Config">The bound values.</param>
/// <param name="Bytes">The frame block, <see cref="ShaderPipelineParameterLayout.SizeBytes"/> long, with every frame
/// member zero.</param>
public sealed record ShaderPipelineParameterValues(
    ShaderConfigValues Config,
    ReadOnlyMemory<byte> Bytes
);
