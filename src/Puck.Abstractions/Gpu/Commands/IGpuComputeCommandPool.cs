namespace Puck.Abstractions.Gpu;

/// <summary>
/// A backend-neutral source of a single compute command buffer, owned for its lifetime.
/// </summary>
public interface IGpuComputeCommandPool : IDisposable {
    /// <summary>Gets the native command-buffer handle to record into.</summary>
    nint CommandBufferHandle { get; }
}
