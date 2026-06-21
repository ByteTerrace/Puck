namespace Puck.Abstractions;

/// <summary>
/// A backend-neutral compute pipeline owning its native pipeline, pipeline-layout, and descriptor-set-layout
/// handles for its lifetime.
/// </summary>
public interface IGpuComputePipeline : IDisposable {
    /// <summary>Gets the native descriptor-set-layout handle a descriptor set is allocated against.</summary>
    nint DescriptorSetLayoutHandle { get; }
    /// <summary>Gets the native compute pipeline handle to bind.</summary>
    nint Handle { get; }
    /// <summary>Gets the native pipeline-layout handle push constants and descriptor sets are bound through.</summary>
    nint LayoutHandle { get; }
}
