namespace Puck.Abstractions.Gpu;

/// <summary>
/// Specifies a bitmask of memory accesses bounding a barrier's synchronization scope, for compute and graphics work alike.
/// </summary>
[Flags]
public enum GpuAccess : uint {
    /// <summary>No access.</summary>
    None = 0,
    /// <summary>A shader read.</summary>
    ShaderRead = 0x1,
    /// <summary>A shader write.</summary>
    ShaderWrite = 0x2,
    /// <summary>A read of indirect dispatch/draw arguments by the GPU command processor (Vulkan
    /// <c>INDIRECT_COMMAND_READ</c>; Direct3D 12 <c>INDIRECT_ARGUMENT</c>). Pair with <see cref="GpuStage.DrawIndirect"/>.</summary>
    IndirectCommandRead = 0x4,
    /// <summary>A transfer operation writes the resource.</summary>
    TransferWrite = 0x8,
    /// <summary>A color attachment writes the resource.</summary>
    ColorAttachmentWrite = 0x10,
    /// <summary>A render pass reads a color attachment's contents, which it loads.</summary>
    ColorAttachmentRead = 0x20,
    /// <summary>A depth test reads a depth attachment.</summary>
    DepthAttachmentRead = 0x40,
    /// <summary>A depth test writes a depth attachment.</summary>
    DepthAttachmentWrite = 0x80,
}
