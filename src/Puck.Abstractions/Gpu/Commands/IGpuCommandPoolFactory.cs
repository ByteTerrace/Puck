namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates backend-neutral command pools.
/// </summary>
public interface IGpuCommandPoolFactory {
    /// <summary>Creates a command pool with a single command buffer.</summary>
    /// <param name="name">The object's debug name, from its creator's identity (<see cref="GpuObjectName"/>); the default value names nothing.</param>
    /// <returns>The created command pool.</returns>
    IGpuCommandPool Create(in GpuObjectName name);
}
