namespace Puck.Abstractions.Gpu;

/// <summary>
/// A backend-neutral shader module handle.
/// </summary>
public interface IGpuShaderModule : IDisposable {
    /// <summary>Gets the native shader module handle.</summary>
    nint Handle { get; }
}
