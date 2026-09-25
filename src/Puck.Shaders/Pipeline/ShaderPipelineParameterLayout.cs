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
/// A pass's frame data: its <see cref="ShaderFrameInterface"/> interface, where the interface's layout places every
/// member, and the config schema its config fields bind through. A document pass reads two blocks: the frame group's,
/// which every pass of a node shares (<see cref="FrameBlockSizeBytes"/>, written by <see cref="WriteFrame"/>), and its
/// own pass block, holding its extent (<see cref="WriteExtent"/>) and config (<see cref="TryBind"/>). A pushed layout
/// (<see cref="IsPushed"/>: an engine package or a shader set) holds all of it in the one block it pushes, so both
/// writers write that block. Each writer places a member at the offset the layout gives it, which is the offset the
/// generated declarations read it from.
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
        var pushed = layout.PushedGroup;
        var frameBlock = (pushed ?? layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Frame)));
        var passBlock = (pushed ?? layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Pass)));
        var frameOffsets = Offsets(block: frameBlock);
        var offsets = Offsets(block: passBlock);

        Interface = shaderInterface;
        IsPushed = (pushed is not null);
        FrameBlockSizeBytes = frameBlock.BlockSizeBytes;
        Layout = layout;
        SizeBytes = passBlock.BlockSizeBytes;
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
        m_pointer = frameOffsets[ShaderFrameInterface.Pointer];
        m_tick = frameOffsets[ShaderFrameInterface.Tick];
        m_time = frameOffsets[ShaderFrameInterface.Time];
        m_timeDelta = frameOffsets[ShaderFrameInterface.TimeDelta];
        m_frame = frameOffsets[ShaderFrameInterface.Frame];
        m_tickRate = frameOffsets[ShaderFrameInterface.TickRate];
        m_pointerDown = frameOffsets[ShaderFrameInterface.PointerDown];
        m_pointerPresses = frameOffsets[ShaderFrameInterface.PointerPresses];
        m_cameraPosition = frameOffsets[ShaderFrameInterface.CameraPosition];
        m_cameraFov = frameOffsets[ShaderFrameInterface.CameraFov];
        m_cameraTarget = frameOffsets[ShaderFrameInterface.CameraTarget];
        m_cameraUp = frameOffsets[ShaderFrameInterface.CameraUp];
    }

    /// <summary>Gets the frame group block's size in bytes, a multiple of 16: the block <see cref="WriteFrame"/> writes,
    /// which is the pushed block itself when <see cref="IsPushed"/>.</summary>
    public uint FrameBlockSizeBytes { get; }
    /// <summary>Gets the pass's interface.</summary>
    public ShaderInterface Interface { get; }
    /// <summary>Gets whether one pushed block holds the frame values, the extent and the config, rather than a bound frame
    /// group and a bound pass block.</summary>
    public bool IsPushed { get; }
    /// <summary>Gets where the interface places every member.</summary>
    public ShaderInterfaceLayout Layout { get; }
    /// <summary>Gets the shared config schema, or <see langword="null"/> for no parameters.</summary>
    public IReadOnlyDictionary<string, ShaderConfigField>? Schema { get; }
    /// <summary>Gets the size in bytes, a multiple of 16, of the block holding the extent and config: the pass block, or
    /// the pushed block when <see cref="IsPushed"/>.</summary>
    public uint SizeBytes { get; }
    /// <summary>Gets the config fields in ordinal name order, each at its offset inside the block of
    /// <see cref="SizeBytes"/>.</summary>
    public IReadOnlyList<ShaderPipelineParameterSlot> Slots { get; }

    private static Dictionary<string, uint> Offsets(ShaderInterfaceGroupLayout block) =>
        block.BlockMembers.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static member => member.Offset,
            keySelector: static member => member.Name
        );
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
    /// <summary>Resolves a package's frame block: the frame members, then its config fields, under an interface named
    /// for the package id.</summary>
    /// <param name="package">The package id.</param>
    /// <param name="config">The package's config schema, or <see langword="null"/> for the frame members alone.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="InvalidDataException">The package id spells no interface name, or the schema is invalid or a
    /// field repeats a frame member's name.</exception>
    public static ShaderPipelineParameterLayout ForPackage(string package, IReadOnlyDictionary<string, ShaderConfigField>? config) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: package);
        ShaderConfigBinding.ValidateSchema(
            ownerName: package,
            schema: config
        );

        // A package reads no generated declarations; its interface is named for its package id, each character an
        // interface name cannot hold, such as the period of sdf.world, spelled as a hyphen.
        return Pushed(
            config: config,
            interfaceName: string.Concat(values: package.Select(selector: static character => ((char.IsAsciiLetterLower(c: character) || char.IsAsciiDigit(c: character))
                ? character
                : '-')))
        );
    }
    /// <summary>Resolves a document pass's frame data from its declaration: the interface its source names
    /// (<see cref="ShaderFrameInterface.NameOf"/>), with the frame group, its config and its ports
    /// (<see cref="ShaderPipelinePassPorts"/>) laid out as <see cref="ShaderFrameInterface.ForPass"/> lays them.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="resources">The graph's resource versions by name, which say what each port carries.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pass"/> or <paramref name="resources"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The config schema is invalid, the source's file does not name an
    /// interface, a config field's name is not an identifier or repeats a frame member's, or a port's identifier is not
    /// valid or is shared (<see cref="ShaderPipelinePassPorts.Members"/>).</exception>
    public static ShaderPipelineParameterLayout Resolve(ShaderPipelinePass pass, IReadOnlyDictionary<string, ShaderPipelineResource> resources) {
        ArgumentNullException.ThrowIfNull(argument: pass);
        ShaderConfigBinding.ValidateSchema(
            schema: pass.Config,
            ownerName: pass.Name
        );

        return new(
            schema: pass.Config,
            shaderInterface: ShaderFrameInterface.ForPass(
                config: pass.Config,
                name: ShaderFrameInterface.NameOf(sourcePath: pass.Source),
                ports: ShaderPipelinePassPorts.Members(
                    pass: pass,
                    resources: resources
                )
            )
        );
    }
    /// <summary>Resolves a pushed block of a named interface over a config schema
    /// (<see cref="ShaderFrameInterface.Pushed"/>): an engine package's or a shader set's.</summary>
    /// <param name="interfaceName">The interface's name.</param>
    /// <param name="config">The config schema, or <see langword="null"/> when there is none.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="InvalidDataException"><paramref name="interfaceName"/> is not an interface name, or a config
    /// field's name is not an identifier or repeats a frame member's.</exception>
    public static ShaderPipelineParameterLayout Pushed(string interfaceName, IReadOnlyDictionary<string, ShaderConfigField>? config) =>
        new(
            schema: config,
            shaderInterface: ShaderFrameInterface.Pushed(
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
    /// <summary>Writes the pass's extent into its block (the pass block, or the pushed block when <see cref="IsPushed"/>)
    /// at the offset the interface places it, leaving the rest as it is.</summary>
    /// <param name="block">The block, at least <see cref="SizeBytes"/> long.</param>
    /// <param name="width">The pass's output width, in pixels.</param>
    /// <param name="height">The pass's output height, in pixels.</param>
    /// <exception cref="ArgumentException"><paramref name="block"/> is shorter than <see cref="SizeBytes"/>.</exception>
    public void WriteExtent(Span<byte> block, uint width, uint height) {
        Require(
            block: block,
            sizeBytes: SizeBytes
        );
        WriteUInt32(block: block, offset: m_extent, value: width);
        WriteUInt32(block: block, offset: (m_extent + 4), value: height);
    }
    /// <summary>Writes every frame group value into the frame block (or the pushed block when <see cref="IsPushed"/>) at
    /// the offset the interface places it, leaving the extent, the config fields and the padding as they are.</summary>
    /// <param name="block">The block, at least <see cref="FrameBlockSizeBytes"/> long.</param>
    /// <param name="values">The host's frame values.</param>
    /// <param name="tick">The engine tick the frame presents.</param>
    /// <param name="frame">The frames the node submitted before this one; only the low 32 bits are written.</param>
    /// <exception cref="ArgumentException"><paramref name="block"/> is shorter than
    /// <see cref="FrameBlockSizeBytes"/>.</exception>
    public void WriteFrame(Span<byte> block, in ShaderFrameValues values, ulong tick, ulong frame) {
        Require(
            block: block,
            sizeBytes: FrameBlockSizeBytes
        );
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

    private static void Require(Span<byte> block, uint sizeBytes) {
        if (block.Length < sizeBytes) {
            throw new ArgumentException(
                message: $"A frame block holds {sizeBytes} bytes; the destination holds {block.Length}.",
                paramName: nameof(block)
            );
        }
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
