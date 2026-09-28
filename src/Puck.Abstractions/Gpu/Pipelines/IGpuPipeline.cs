namespace Puck.Abstractions.Gpu;

/// <summary>
/// A backend-neutral graphics pipeline: the pipeline state object, its layout, and its descriptor set layouts.
/// </summary>
public interface IGpuPipeline : IDisposable {
    /// <summary>Gets the native descriptor set layout handle of a pipeline created without a
    /// <see cref="GpuGraphicsPipelineDescription.Layout"/>.</summary>
    nint DescriptorSetLayoutHandle { get; }
    /// <summary>Gets the native layout handle a set of each group is allocated against, indexed by the group's ordinal
    /// and zero where the pipeline binds no group, for a pipeline created from a
    /// <see cref="GpuGraphicsPipelineDescription.Layout"/>; empty for one created without it.</summary>
    IReadOnlyList<nint> GroupLayoutHandles { get; }
    /// <summary>Gets the native pipeline handle.</summary>
    nint Handle { get; }
    /// <summary>Gets the native pipeline layout handle.</summary>
    nint LayoutHandle { get; }
}
