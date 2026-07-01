namespace Puck.Abstractions.Gpu;

/// <summary>
/// Specifies a bitmask of memory accesses bounding a compute barrier's synchronization scope.
/// </summary>
public static class GpuComputeAccess {
    /// <summary>No access.</summary>
    public const uint None = 0;
    /// <summary>A shader read.</summary>
    public const uint ShaderRead = 0x1;
    /// <summary>A shader write.</summary>
    public const uint ShaderWrite = 0x2;
    /// <summary>A read of indirect dispatch/draw arguments by the GPU command processor (Vulkan
    /// <c>INDIRECT_COMMAND_READ</c>; Direct3D 12 <c>INDIRECT_ARGUMENT</c>). Pair with <see cref="GpuComputeStage.DrawIndirect"/>.</summary>
    public const uint IndirectCommandRead = 0x4;
}
