using System.Buffers.Binary;
using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Gpu;

/// <summary>The one group the display encode binds (<c>Assets/Runtime/display-encode.frag.hlsl</c> in
/// <c>Puck.Shaders</c>): the working image as a sampled image, its sampler and the encode block, in the pass group. A
/// register number equals its binding and the group's ordinal is its register space, so the shader declares the image at
/// <c>register(t0, space3)</c>, the sampler at <c>register(s1, space3)</c> and the block at <c>register(b2, space3)</c>
/// on both backends. Every swapchain compositor and the float preview and capture encode bind it.</summary>
public static class DisplayEncodeLayout {
    /// <summary>The group's ordinal: the pass group's, which is its Vulkan set and its Direct3D 12 register space.</summary>
    public const uint Group = 3;
    /// <summary>The sampler's binding.</summary>
    public const uint SamplerBinding = 1;
    /// <summary>The working image's binding.</summary>
    public const uint SourceImageBinding = 0;
    /// <summary>The encode block's binding.</summary>
    public const uint BlockBinding = 2;
    /// <summary>The encode block's size, in bytes: the color space, the white scale and padding to one 16-byte
    /// row.</summary>
    public const int BlockBytes = 16;

    /// <summary>Gets the encode's pipeline layout: the one group, read by the vertex and fragment stages, pushing
    /// nothing.</summary>
    public static GpuPipelineLayoutDescription Layout { get; } = new(
        groups: [new GpuGroupLayoutDescription(
            bindings: [
                new GpuGroupBinding(
                    binding: SourceImageBinding,
                    kind: GpuBindingKind.SampledImage
                ),
                new GpuGroupBinding(
                    binding: SamplerBinding,
                    kind: GpuBindingKind.Sampler
                ),
                new GpuGroupBinding(
                    binding: BlockBinding,
                    kind: GpuBindingKind.ConstantBuffer
                ),
            ],
            ordinal: Group
        )],
        pushesIndex: false,
        stages: GpuShaderStage.Vertex | GpuShaderStage.Fragment
    );

    /// <summary>Writes the encode block for a display output: its color space as a <c>uint</c>, then the linear value
    /// SDR white takes in it at the paper-white level (<see cref="DisplayOutput.WhiteScale"/>) as a <c>float</c>, little
    /// endian, the rest zero.</summary>
    /// <param name="block">The block's bytes, at least <see cref="BlockBytes"/> long.</param>
    /// <param name="output">The output the encode writes for.</param>
    /// <param name="paperWhiteNits">The paper-white level, in nits.</param>
    /// <exception cref="ArgumentException"><paramref name="block"/> is shorter than <see cref="BlockBytes"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="paperWhiteNits"/> is outside the range
    /// <see cref="DisplayOutput.RequirePaperWhite"/> accepts.</exception>
    public static void WriteBlock(Span<byte> block, DisplayOutput output, double paperWhiteNits) {
        if (block.Length < BlockBytes) {
            throw new ArgumentException(
                message: $"The encode block is {BlockBytes} bytes.",
                paramName: nameof(block)
            );
        }

        var whiteScale = ((float)output.WhiteScale(paperWhiteNits: paperWhiteNits));

        block[..BlockBytes].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: block,
            value: ((uint)output.ColorSpace)
        );
        BinaryPrimitives.WriteSingleLittleEndian(
            destination: block[sizeof(uint)..],
            value: whiteScale
        );
    }
}
