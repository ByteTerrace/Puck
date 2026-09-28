using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>Creates timestamp pools on the graphics queue, with its actual precision and the device's timestamp period.</summary>
/// <param name="deviceContext">The owning device context.</param>
/// <param name="naming">The object's debug naming.</param>
public sealed unsafe class VulkanGpuTimestampFactory(IVulkanDeviceContext deviceContext, GpuObjectNaming naming) : IGpuTimestampFactory {
    // VkPhysicalDeviceProperties.limits starts at 296; timestampPeriod is at 424 within VkPhysicalDeviceLimits.
    // The installed Vulkan 1.4 header's native alignment gives absolute byte offset 720, like the other limits reads.
    internal const int TimestampPeriodOffset = 720;

    /// <inheritdoc/>
    public IGpuTimestampPool? Create(uint count, in GpuObjectName name) {
        ArgumentOutOfRangeException.ThrowIfZero(value: count);
        var logical = deviceContext.LogicalDevice;
        var physical = deviceContext.PhysicalDevice;
        var instance = deviceContext.Instance.Commands;
        var families = 0U;

        instance.GetPhysicalDeviceQueueFamilyProperties(physical.Handle, ref families, 0);
        var properties = new VkQueueFamilyProperties[families];

        fixed (VkQueueFamilyProperties* rows = properties) {
            instance.GetPhysicalDeviceQueueFamilyProperties(physical.Handle, ref families, ((nint)rows));
        }
        var bits = properties[physical.QueueFamilySelection.GraphicsFamilyIndex].TimestampValidBits;

        if (bits == 0) { return null; }
        var deviceProperties = new VkPhysicalDeviceProperties2();

        instance.GetPhysicalDeviceProperties(physical.Handle, ((nint)deviceProperties.Properties));
        var period = *((float*)(deviceProperties.Properties + TimestampPeriodOffset));
        var info = new VkQueryPoolCreateInfo { QueryCount = count, QueryType = 2, SType = 11 };

        logical.Commands.Timestamps.CreateQueryPool(logical.Commands.Handle, in info, 0, out var handle).ThrowIfFailed(operation: "vkCreateQueryPool");
        naming.Name(handle: handle, kind: GpuObjectKind.TimestampPool, name: in name);
        return new Pool(bits: bits, device: logical, handle: handle, period: period);
    }

    private sealed class Pool(VulkanLogicalDevice device, nint handle, uint bits, float period) : IGpuTimestampPool {
        private nint m_handle = handle;

        public double NanosecondsPerTick => period;
        public uint ValidBits => bits;

        public void Reset(nint command, uint first, uint count) => device.Commands.Timestamps.CmdResetQueryPool(command, m_handle, first, count);
        public void Write(nint command, uint index) => device.Commands.Timestamps.CmdWriteTimestamp(command, 0x00002000, m_handle, index);
        public void Resolve(nint command, uint first, uint count, nint destination, ulong offset) =>
            device.Commands.Timestamps.CmdCopyQueryPoolResults(command, m_handle, first, count, destination, offset, 8, 3);
        public void Dispose() {
            if (!device.IsDisposed) { device.Commands.Destroy(destroy: device.Commands.Timestamps.DestroyQueryPool, handle: m_handle); }
            m_handle = 0;
        }
    }
}
