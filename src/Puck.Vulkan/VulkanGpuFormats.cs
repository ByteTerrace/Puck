namespace Puck.Vulkan;

internal static class VulkanGpuFormats {
    internal static uint ToVkFormat(uint gpuPixelFormat) => gpuPixelFormat switch {
        GpuPixelFormat.R8G8B8A8Unorm => VulkanFormat.R8G8B8A8Unorm,
        GpuPixelFormat.B8G8R8A8Unorm => VulkanFormat.B8G8R8A8Unorm,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(gpuPixelFormat), actualValue: gpuPixelFormat, message: null),
    };
}
