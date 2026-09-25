namespace Puck.Abstractions.Gpu;

/// <summary>
/// A backend-neutral compute pipeline owning its native pipeline, pipeline-layout, and descriptor-set-layout
/// handles for its lifetime.
/// </summary>
public interface IGpuComputePipeline : IDisposable {
    /// <summary>Gets the native descriptor-set-layout handle a descriptor set is allocated against, for a pipeline
    /// created without a <see cref="GpuComputePipelineDescription.Layout"/>.</summary>
    nint DescriptorSetLayoutHandle { get; }
    /// <summary>Gets the native layout handle a set of each group is allocated against, indexed by the group's ordinal
    /// and zero where the pipeline binds no group, for a pipeline created from a
    /// <see cref="GpuComputePipelineDescription.Layout"/>; empty for one created without it.</summary>
    IReadOnlyList<nint> GroupLayoutHandles { get; }
    /// <summary>Gets the native compute pipeline handle to bind.</summary>
    nint Handle { get; }
    /// <summary>Gets the native pipeline-layout handle push constants and descriptor sets are bound through.</summary>
    nint LayoutHandle { get; }
}
