using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanCommandResourcesApi"/>, marshaling to the command-pool
/// and command-buffer allocation entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeCommandResourcesApi : IVulkanCommandResourcesApi {
    // VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT: lets callers re-record individual
    // command buffers one at a time, since vkBeginCommandBuffer may implicitly reset a
    // single buffer only when its pool carries this flag.
    private const uint CommandPoolCreateResetCommandBufferFlag = 0x00000002;
    private const uint PrimaryCommandBufferLevel = 0;
    private const uint StructureTypeCommandBufferAllocateInfo = 40;
    private const uint StructureTypeCommandPoolCreateInfo = 39;

    /// <inheritdoc/>
    public VkResult AllocateCommandBuffers(VulkanCommandBufferAllocateRequest request, nint buffer, uint commandBufferCount) {
        ArgumentNullException.ThrowIfNull(
            argument: request.Device,
            paramName: nameof(request)
        );

        var allocateCommandBuffers = request.Device.AllocateCommandBuffers;
        var allocateInfo = new VkCommandBufferAllocateInfo {
            CommandBufferCount = commandBufferCount,
            CommandPool = request.CommandPoolHandle,
            Level = PrimaryCommandBufferLevel,
            SType = StructureTypeCommandBufferAllocateInfo,
        };

        return allocateCommandBuffers(
            request.Device.Handle,
            in allocateInfo,
            buffer
        );
    }
    /// <inheritdoc/>
    public VkResult CreateCommandPool(VulkanCommandPoolCreateRequest request, out nint commandPoolHandle) {
        ArgumentNullException.ThrowIfNull(
            argument: request.Device,
            paramName: nameof(request)
        );

        var createCommandPool = request.Device.CreateCommandPool;
        var createInfo = new VkCommandPoolCreateInfo {
            Flags = CommandPoolCreateResetCommandBufferFlag,
            QueueFamilyIndex = request.QueueFamilyIndex,
            SType = StructureTypeCommandPoolCreateInfo,
        };

        return createCommandPool(
            request.Device.Handle,
            in createInfo,
            0,
            out commandPoolHandle
        );
    }
    /// <inheritdoc/>
    public void DestroyCommandPool(VulkanDeviceCommands device, nint commandPoolHandle) =>
        device?.Destroy(
            destroy: device.DestroyCommandPool,
            handle: commandPoolHandle
        );
}
