using Puck.Abstractions.Gpu;

namespace Puck.Overlays;

/// <summary>
/// The unified overlay fragment pass's binding layout, identical on both backends and read by
/// <c>overlay-unified.frag.hlsl</c>: binding 0 the inner world image, 1 to <see cref="OverlayFrameSlots.SlotCount"/> the
/// frame-slot table (one scalar texture and sampler pair per binding, since DXC's combined image sampler never fuses an
/// array), then the program storage buffer at the next binding, immediately after every sampler. Vulkan declares the
/// sampler bindings followed by the storage buffer (<c>VulkanGraphicsPipelineFactory.BuildDescriptorBindings</c>); the
/// Direct3D 12 graphics root signature packs its descriptor table with an identity binding-to-slot map
/// (<c>DirectXGpuPipelineFactory.BuildLayout</c>). Every sampler binding shares one sampler, and an unbound frame slot
/// is written with the world image, so every binding the shader's slot-selecting switch reaches is valid.
/// </summary>
public static class OverlayPassLayout {
    /// <summary>The binding of the inner world image.</summary>
    public const uint SamplerBinding = 0;
    /// <summary>The binding of the first frame slot.</summary>
    public const uint FrameSlotFirstBinding = (SamplerBinding + 1);
    /// <summary>The number of combined image-sampler bindings: the world image and every frame slot.</summary>
    public const uint TextureSamplerCount = (1u + OverlayFrameSlots.SlotCount);
    /// <summary>The binding of the program storage buffer, after every sampler.</summary>
    public const uint StorageBufferBinding = TextureSamplerCount;
    /// <summary>The byte length of the push block: counts, glyph figures and region bases, three float4s, which the
    /// shader's <c>OverlayPassData</c> declares.</summary>
    public const int PushConstantBytes = ((sizeof(float) * 4) * 3);
    /// <summary>The stride, in bytes, of one element of the storage buffer the shader reads.</summary>
    public const uint StorageElementStrideBytes = (4 * sizeof(uint));

    /// <summary>Gets the bindings of one descriptor set of the pass: every combined image sampler, then the storage
    /// buffer.</summary>
    public static IReadOnlyList<GpuComputeBinding> SetBindings { get; } = [
        .. Enumerable.Range(
            count: ((int)TextureSamplerCount),
            start: ((int)SamplerBinding)
        ).Select(selector: static binding => new GpuComputeBinding(
            ((uint)binding),
            GpuComputeBindingKind.SampledImage
        )),
        new GpuComputeBinding(
            StorageBufferBinding,
            GpuComputeBindingKind.StorageBufferRead
        ),
    ];

    /// <summary>Returns the pass's graphics pipeline description: the fullscreen triangle's vertex input, every sampler,
    /// the storage buffer and the fragment push block.</summary>
    /// <returns>The description.</returns>
    public static GpuGraphicsPipelineDescription PipelineDescription() => new(
        Name: "overlay-unified",
        VertexInput: new GpuVertexInputLayout(
            StrideBytes: FullscreenTriangle.StrideBytes,
            Attributes: [new GpuVertexAttribute(
                Format: GpuVertexFormat.R32G32Float,
                Location: 0,
                OffsetBytes: 0
            )]
        ),
        TextureSamplerCount: TextureSamplerCount,
        EnableStorageBuffer: true,
        PushConstantBinding: new GpuPushConstantBinding(
            data: new byte[PushConstantBytes],
            offset: 0,
            stageFlags: GpuShaderStage.Fragment
        )
    );
}
