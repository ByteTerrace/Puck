using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// What one pass pipeline is built from and what determines it: a compute pipeline's bytecode and description, or a
/// graphics pipeline's two stages' bytecode, the render pass it is created for and its description. Two keys are equal
/// exactly when their <see cref="ContentKey"/>s are, which <see cref="GpuPipelineCacheStore.ContentKeyOf"/> hashes from
/// the bytecode and a canonical encoding of every field of the description and render pass, the description's name
/// included, so a changed kernel, layout, attachment format or depth test is a different pipeline and the same pass
/// installed twice is the same one. The key holds the bytecode it was made from; nothing it holds is read after the
/// build.
/// </summary>
public sealed class GpuPassPipelineKey : IEquatable<GpuPassPipelineKey> {
    private GpuPassPipelineKey(string name, string contentKey, ReadOnlyMemory<byte> primary, ReadOnlyMemory<byte> secondary, GpuComputePipelineDescription? compute, GpuGraphicsPipelineDescription? graphics, GpuRenderPassDescription? renderPass) {
        Compute = compute;
        ContentKey = contentKey;
        Graphics = graphics;
        Name = name;
        Primary = primary;
        RenderPass = renderPass;
        Secondary = secondary;
    }

    /// <summary>Gets the compute pipeline's description, or <see langword="null"/> for a graphics pipeline.</summary>
    public GpuComputePipelineDescription? Compute { get; }
    /// <summary>Gets the lowercase 16-digit content key the key is equal by, which also names the entry's objects.</summary>
    public string ContentKey { get; }
    /// <summary>Gets the graphics pipeline's description, or <see langword="null"/> for a compute pipeline.</summary>
    public GpuGraphicsPipelineDescription? Graphics { get; }
    /// <summary>Gets the description's name, the part every object the entry creates is named by.</summary>
    public string Name { get; }
    /// <summary>Gets the compute stage's bytecode, or the vertex stage's for a graphics pipeline.</summary>
    public ReadOnlyMemory<byte> Primary { get; }
    /// <summary>Gets the render pass a graphics pipeline is created for, or <see langword="null"/> for a compute
    /// pipeline.</summary>
    public GpuRenderPassDescription? RenderPass { get; }
    /// <summary>Gets the fragment stage's bytecode of a graphics pipeline; empty for a compute pipeline.</summary>
    public ReadOnlyMemory<byte> Secondary { get; }

    /// <summary>Returns the key of a compute pipeline.</summary>
    /// <param name="bytecode">The compute stage's bytecode for the device's backend; not empty.</param>
    /// <param name="description">The pipeline's description.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="bytecode"/> is empty.</exception>
    public static GpuPassPipelineKey OfCompute(ReadOnlyMemory<byte> bytecode, GpuComputePipelineDescription description) {
        ArgumentNullException.ThrowIfNull(argument: description);
        RequireBytecode(
            bytecode: bytecode,
            name: description.Name,
            paramName: nameof(bytecode)
        );

        var state = new ArrayBufferWriter<byte>();

        Write(
            description: description,
            writer: state
        );

        return new GpuPassPipelineKey(
            compute: description,
            contentKey: GpuPipelineCacheStore.ContentKeyOf(bytecode, state.WrittenMemory),
            graphics: null,
            name: description.Name,
            primary: bytecode,
            renderPass: null,
            secondary: ReadOnlyMemory<byte>.Empty
        );
    }
    /// <summary>Returns the key of a graphics pipeline.</summary>
    /// <param name="vertex">The vertex stage's bytecode for the device's backend; not empty.</param>
    /// <param name="fragment">The fragment stage's bytecode for the device's backend; not empty.</param>
    /// <param name="renderPass">The render pass the pipeline draws in, which the entry creates beside it.</param>
    /// <param name="description">The pipeline's description.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="renderPass"/> or <paramref name="description"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="vertex"/> or <paramref name="fragment"/> is empty.</exception>
    public static GpuPassPipelineKey OfGraphics(ReadOnlyMemory<byte> vertex, ReadOnlyMemory<byte> fragment, GpuRenderPassDescription renderPass, GpuGraphicsPipelineDescription description) {
        ArgumentNullException.ThrowIfNull(argument: renderPass);
        ArgumentNullException.ThrowIfNull(argument: description);
        RequireBytecode(
            bytecode: vertex,
            name: description.Name,
            paramName: nameof(vertex)
        );
        RequireBytecode(
            bytecode: fragment,
            name: description.Name,
            paramName: nameof(fragment)
        );

        var state = new ArrayBufferWriter<byte>();

        Write(
            description: description,
            writer: state
        );
        Write(
            renderPass: renderPass,
            writer: state
        );

        return new GpuPassPipelineKey(
            compute: null,
            contentKey: GpuPipelineCacheStore.ContentKeyOf(vertex, fragment, state.WrittenMemory),
            graphics: description,
            name: description.Name,
            primary: vertex,
            renderPass: renderPass,
            secondary: fragment
        );
    }
    /// <inheritdoc/>
    public bool Equals(GpuPassPipelineKey? other) =>
        ((other is not null) && string.Equals(
            a: ContentKey,
            b: other.ContentKey,
            comparisonType: StringComparison.Ordinal
        ));
    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        Equals(other: (obj as GpuPassPipelineKey));
    /// <inheritdoc/>
    public override int GetHashCode() =>
        StringComparer.Ordinal.GetHashCode(obj: ContentKey);

    private static void RequireBytecode(ReadOnlyMemory<byte> bytecode, string name, string paramName) {
        if (bytecode.IsEmpty) {
            throw new ArgumentException(
                message: $"Pass pipeline '{name}' has an empty stage.",
                paramName: paramName
            );
        }
    }
    private static void Write(GpuComputePipelineDescription description, ArrayBufferWriter<byte> writer) {
        Write(
            text: description.Name,
            writer: writer
        );
        Write(
            value: ((uint)description.Bindings.Count),
            writer: writer
        );

        foreach (var binding in description.Bindings) {
            Write(value: binding.Binding, writer: writer);
            Write(value: ((uint)binding.Kind), writer: writer);
            Write(value: binding.Count, writer: writer);
        }

        Write(
            push: description.PushConstantBinding,
            writer: writer
        );
        Write(
            layout: description.Layout,
            writer: writer
        );
    }
    private static void Write(GpuGraphicsPipelineDescription description, ArrayBufferWriter<byte> writer) {
        Write(
            text: description.Name,
            writer: writer
        );
        Write(value: description.VertexInput.StrideBytes, writer: writer);
        Write(value: ((uint)description.VertexInput.Attributes.Count), writer: writer);

        foreach (var attribute in description.VertexInput.Attributes) {
            Write(value: attribute.Location, writer: writer);
            Write(value: ((uint)attribute.Format), writer: writer);
            Write(value: attribute.OffsetBytes, writer: writer);
        }

        Write(value: ((description.DepthCompare is { } compare) ? (((uint)compare) + 1u) : 0u), writer: writer);
        Write(
            layout: description.Layout,
            writer: writer
        );
    }
    private static void Write(GpuRenderPassDescription renderPass, ArrayBufferWriter<byte> writer) {
        Write(value: ((uint)renderPass.Colors.Count), writer: writer);

        foreach (var color in renderPass.Colors) {
            Write(value: ((uint)color.Format), writer: writer);
            Write(value: ((uint)color.Load), writer: writer);
            Write(value: ((uint)color.Store), writer: writer);
            Write(value: ((uint)color.FinalLayout), writer: writer);
        }

        if (renderPass.Depth is { } depth) {
            Write(value: 1u, writer: writer);
            Write(value: ((uint)depth.Format), writer: writer);
            Write(value: ((uint)depth.Load), writer: writer);
            Write(value: ((uint)depth.Store), writer: writer);
            Write(value: BitConverter.SingleToUInt32Bits(value: depth.ClearDepth), writer: writer);
        } else {
            Write(value: 0u, writer: writer);
        }
    }
    // A push range is its offset, size and stages; the data it starts with is not part of the pipeline.
    private static void Write(GpuPushConstantBinding? push, ArrayBufferWriter<byte> writer) {
        if (push is null) {
            Write(value: 0u, writer: writer);

            return;
        }

        Write(value: 1u, writer: writer);
        Write(value: push.Offset, writer: writer);
        Write(value: push.Size, writer: writer);
        Write(value: ((uint)push.StageFlags), writer: writer);
    }
    private static void Write(GpuPipelineLayoutDescription? layout, ArrayBufferWriter<byte> writer) {
        if (layout is null) {
            Write(value: 0u, writer: writer);

            return;
        }

        Write(value: 1u, writer: writer);
        Write(value: ((uint)layout.Stages), writer: writer);
        Write(value: (layout.PushesIndex ? 1u : 0u), writer: writer);
        Write(value: ((uint)layout.Groups.Count), writer: writer);

        foreach (var group in layout.Groups) {
            Write(value: group.Ordinal, writer: writer);
            Write(value: ((uint)group.Bindings.Count), writer: writer);

            foreach (var binding in group.Bindings) {
                Write(value: binding.Binding, writer: writer);
                Write(value: ((uint)binding.Kind), writer: writer);
                Write(value: binding.Count, writer: writer);
            }
        }
    }
    private static void Write(string text, ArrayBufferWriter<byte> writer) {
        var count = Encoding.UTF8.GetByteCount(s: text);

        Write(value: ((uint)count), writer: writer);
        Encoding.UTF8.GetBytes(
            bytes: writer.GetSpan(sizeHint: count),
            chars: text.AsSpan()
        );
        writer.Advance(count: count);
    }
    private static void Write(uint value, ArrayBufferWriter<byte> writer) {
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: writer.GetSpan(sizeHint: sizeof(uint)),
            value: value
        );
        writer.Advance(count: sizeof(uint));
    }
}
