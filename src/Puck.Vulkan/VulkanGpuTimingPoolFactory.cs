using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuTimingPoolFactory"/> for Vulkan: creates timestamp <c>VkQueryPool</c>s and reports the
/// device's timestamp capabilities. <see cref="GetCapabilities"/> re-derives the graphics queue family (the valid-bit
/// count is per-family) since the device context does not carry the family index.
/// </summary>
public sealed class VulkanGpuTimingPoolFactory(IVulkanQueryPoolApi queryPoolApi, IVulkanPhysicalDeviceApi physicalDeviceApi) : IGpuTimingPoolFactory {
    /// <inheritdoc/>
    public IGpuTimingPool CreateTimestampPool(IGpuDeviceContext deviceContext, uint queryCapacity) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        var deviceHandle = ((IVulkanDeviceContext)deviceContext).LogicalDevice.Handle;

        queryPoolApi.CreateTimestampPool(
            deviceHandle: deviceHandle,
            queryCount: queryCapacity,
            queryPoolHandle: out var poolHandle
        ).ThrowIfFailed(operation: "vkCreateQueryPool");

        return new VulkanGpuTimingPool(
            capacity: queryCapacity,
            deviceHandle: deviceHandle,
            poolHandle: poolHandle,
            queryPoolApi: queryPoolApi
        );
    }

    /// <inheritdoc/>
    public GpuTimestampCapabilities GetCapabilities(IGpuDeviceContext deviceContext) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        var context = (IVulkanDeviceContext)deviceContext;
        var instanceHandle = context.Instance.Handle;
        var physicalDeviceHandle = context.PhysicalDevice.Handle;
        var graphicsQueueFamilyIndex = 0u;

        foreach (var family in physicalDeviceApi.GetQueueFamilies(instanceHandle: instanceHandle, physicalDeviceHandle: physicalDeviceHandle)) {
            if (family.Flags.HasFlag(flag: VkQueueFlags.Graphics)) {
                graphicsQueueFamilyIndex = family.Index;

                break;
            }
        }

        var capabilities = physicalDeviceApi.GetTimestampCapabilities(
            graphicsQueueFamilyIndex: graphicsQueueFamilyIndex,
            instanceHandle: instanceHandle,
            physicalDeviceHandle: physicalDeviceHandle
        );

        return new GpuTimestampCapabilities(
            PeriodNanoseconds: capabilities.PeriodNanoseconds,
            ValidBits: capabilities.GraphicsQueueValidBits
        );
    }
}
