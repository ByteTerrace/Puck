namespace Puck.Abstractions.Gpu;

/// <summary>The faces a graphics pipeline's rasterizer discards before the fragment stage, a face being front when its
/// triangle appears counter-clockwise on the attachment (Vulkan <c>VkCullModeFlags</c>; Direct3D 12
/// <c>D3D12_CULL_MODE</c>).</summary>
public enum GpuCullMode : uint {
    /// <summary>No face is discarded.</summary>
    None = 0,
    /// <summary>A back face is discarded.</summary>
    Back = 1,
}
