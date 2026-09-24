using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanFrameSynchronizationApi"/>, marshaling to the fence and
/// semaphore entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeFrameSynchronizationApi : IVulkanFrameSynchronizationApi {
    private const uint FenceCreateSignaledBit = 0x00000001;
    private const uint StructureTypeFenceCreateInfo = 8;
    private const uint StructureTypeSemaphoreCreateInfo = 9;

    /// <inheritdoc/>
    public VkResult CreateFence(VulkanFrameSynchronizationCreateRequest request, out nint fenceHandle) {
        ArgumentNullException.ThrowIfNull(
            argument: request.Device,
            paramName: nameof(request)
        );

        var createFence = request.Device.CreateFence;
        var createInfo = new VkFenceCreateInfo {
            Flags = (request.StartSignaled
            ? FenceCreateSignaledBit
            : 0),
            SType = StructureTypeFenceCreateInfo,
        };

        return createFence(
            request.Device.Handle,
            in createInfo,
            0,
            out fenceHandle
        );
    }
    /// <inheritdoc/>
    public VkResult CreateSemaphore(VulkanFrameSynchronizationCreateRequest request, out nint semaphoreHandle) {
        ArgumentNullException.ThrowIfNull(
            argument: request.Device,
            paramName: nameof(request)
        );

        var createSemaphore = request.Device.CreateSemaphore;
        var createInfo = new VkSemaphoreCreateInfo { SType = StructureTypeSemaphoreCreateInfo };

        return createSemaphore(
            request.Device.Handle,
            in createInfo,
            0,
            out semaphoreHandle
        );
    }
    /// <inheritdoc/>
    public void DestroyFence(VulkanDeviceCommands device, nint fenceHandle) =>
        device?.Destroy(
            destroy: device.DestroyFence,
            handle: fenceHandle
        );
    /// <inheritdoc/>
    public void DestroySemaphore(VulkanDeviceCommands device, nint semaphoreHandle) =>
        device?.Destroy(
            destroy: device.DestroySemaphore,
            handle: semaphoreHandle
        );
    /// <inheritdoc/>
    public VkResult GetFenceStatus(VulkanDeviceCommands device, nint fenceHandle) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: fenceHandle,
            handleDescription: "fence",
            paramName: nameof(fenceHandle)
        );

        var getFenceStatus = device.GetFenceStatus;

        return getFenceStatus(
            device.Handle,
            fenceHandle
        );
    }
    /// <inheritdoc/>
    public VkResult ResetFence(VulkanDeviceCommands device, nint fenceHandle) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: fenceHandle,
            handleDescription: "fence",
            paramName: nameof(fenceHandle)
        );

        var resetFences = device.ResetFences;

        return resetFences(
            device.Handle,
            1,
            in fenceHandle
        );
    }
    /// <inheritdoc/>
    public VkResult WaitForFence(VulkanDeviceCommands device, nint fenceHandle, ulong timeout) {
        ArgumentNullException.ThrowIfNull(argument: device);

        VulkanArgument.RequireHandle(
            handle: fenceHandle,
            handleDescription: "fence",
            paramName: nameof(fenceHandle)
        );

        var waitForFences = device.WaitForFences;

        return waitForFences(
            device.Handle,
            1,
            in fenceHandle,
            1,
            timeout
        );
    }
}
