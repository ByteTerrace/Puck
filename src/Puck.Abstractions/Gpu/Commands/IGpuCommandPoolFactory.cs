namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral command pools.
/// </summary>
public interface IGpuCommandPoolFactory {
    /// <summary>Creates a command pool with a single command buffer.</summary>
    /// <returns>The created command pool.</returns>
    IGpuCommandPool Create();
}
