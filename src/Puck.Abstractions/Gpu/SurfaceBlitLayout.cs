namespace Puck.Abstractions.Gpu;

/// <summary>The one group both swapchain compositors' blits bind: the source surface as a sampled image and its sampler,
/// in the pass group. A register number equals its binding and the group's ordinal is its register space, so the blit
/// shaders declare the image at <c>register(t0, space3)</c> and the sampler at <c>register(s1, space3)</c> on both
/// backends.</summary>
public static class SurfaceBlitLayout {
    /// <summary>The group's ordinal: the pass group's, which is its Vulkan set and its Direct3D 12 register space.</summary>
    public const uint Group = 3;
    /// <summary>The sampler's binding.</summary>
    public const uint SamplerBinding = 1;
    /// <summary>The source surface's binding.</summary>
    public const uint SourceImageBinding = 0;

    /// <summary>Gets the blit's pipeline layout: the one group, read by the vertex and fragment stages, pushing
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
            ],
            ordinal: Group
        )],
        pushesIndex: false,
        stages: GpuShaderStage.Vertex | GpuShaderStage.Fragment
    );
}
