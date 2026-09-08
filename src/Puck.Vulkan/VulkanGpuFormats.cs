namespace Puck.Vulkan;

/// <summary>Maps backend-neutral <see cref="GpuPixelFormat"/> values to their <c>VkFormat</c> equivalents.</summary>
public static class VulkanGpuFormats {
    /// <summary>Converts a <see cref="GpuPixelFormat"/> to its <c>VkFormat</c> value.</summary>
    /// <param name="gpuPixelFormat">The backend-neutral pixel format.</param>
    /// <returns>The corresponding <see cref="VulkanFormat"/> constant.</returns>
    public static uint ToVkFormat(GpuPixelFormat gpuPixelFormat) => gpuPixelFormat switch {
        GpuPixelFormat.R8G8B8A8Unorm => VulkanFormat.R8G8B8A8Unorm,
        GpuPixelFormat.B8G8R8A8Unorm => VulkanFormat.B8G8R8A8Unorm,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(gpuPixelFormat), actualValue: gpuPixelFormat, message: null),
    };
}
