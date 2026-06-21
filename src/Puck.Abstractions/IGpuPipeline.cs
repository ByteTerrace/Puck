namespace Puck.Abstractions;

/// <summary>
/// A backend-neutral graphics pipeline: the pipeline state object, its layout, and its descriptor set layout.
/// </summary>
public interface IGpuPipeline : IDisposable {
    /// <summary>Gets the native descriptor set layout handle.</summary>
    nint DescriptorSetLayoutHandle { get; }
    /// <summary>Gets the native pipeline handle.</summary>
    nint Handle { get; }
    /// <summary>Gets the native pipeline layout handle.</summary>
    nint LayoutHandle { get; }
}
