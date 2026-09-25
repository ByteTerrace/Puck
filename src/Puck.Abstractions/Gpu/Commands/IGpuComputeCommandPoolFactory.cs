namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral compute command pools.
/// </summary>
public interface IGpuComputeCommandPoolFactory {
    /// <summary>Creates a compute command pool with a single command buffer.</summary>
    /// <returns>The created command pool.</returns>
    IGpuComputeCommandPool Create();
}
