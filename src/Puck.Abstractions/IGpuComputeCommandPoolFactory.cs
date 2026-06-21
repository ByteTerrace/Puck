namespace Puck.Abstractions;

/// <summary>
/// Creates backend-neutral compute command pools.
/// </summary>
public interface IGpuComputeCommandPoolFactory {
    /// <summary>Creates a compute command pool with a single command buffer.</summary>
    /// <param name="deviceContext">The device to create the pool on.</param>
    /// <returns>The created command pool.</returns>
    IGpuComputeCommandPool Create(IGpuDeviceContext deviceContext);
}
