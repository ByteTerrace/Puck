namespace Puck.Abstractions.Gpu;

/// <summary>
/// A backend-neutral source of a single command buffer, owned for its lifetime, that records compute and graphics work.
/// </summary>
public interface IGpuCommandPool : IDisposable {
    /// <summary>Gets the native command-buffer handle to record into.</summary>
    nint CommandBufferHandle { get; }
}
