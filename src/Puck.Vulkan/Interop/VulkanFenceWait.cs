using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;

namespace Puck.Vulkan.Interop;

/// <summary>Blocks the calling thread on a <c>VkFence</c> the GPU signals, bounded, so a wait on a device that stopped
/// making progress without reporting a loss ends in the device-loss recovery rather than never. The one blocking fence
/// wait; every other fence read polls or passes a zero timeout.</summary>
public static class VulkanFenceWait {
    /// <summary>The longest <see cref="Wait"/> blocks, in nanoseconds. Operating systems reset a device whose work has not
    /// retired within a couple of seconds (reported by the wait itself as <c>VK_ERROR_DEVICE_LOST</c>), so a fence that
    /// outlasts many times that has no GPU work behind it.</summary>
    public const ulong TimeoutNanoseconds = 30_000_000_000UL;

    /// <summary>Waits for a fence to become signaled for at most <see cref="TimeoutNanoseconds"/>.</summary>
    /// <param name="api">The frame synchronization calls.</param>
    /// <param name="device">The device the fence belongs to.</param>
    /// <param name="fenceHandle">The native <c>VkFence</c> handle waited on.</param>
    /// <returns><see cref="VkResult.Success"/> once the fence is signaled; <see cref="VkResult.ErrorDeviceLost"/> when the
    /// device reported its loss or the fence did not signal within the timeout (written to the error stream); otherwise
    /// the call's error code.</returns>
    public static VkResult Wait(IVulkanFrameSynchronizationApi api, VulkanDeviceCommands device, nint fenceHandle) {
        ArgumentNullException.ThrowIfNull(argument: api);

        var result = api.WaitForFence(
            device: device,
            fenceHandle: fenceHandle,
            timeout: TimeoutNanoseconds
        );

        if (result != VkResult.Timeout) {
            return result;
        }

        Console.Error.WriteLine(value: $"[vulkan] a fence did not signal within {TimeoutNanoseconds / 1_000_000_000UL} seconds; reporting the device lost (hung).");

        return VkResult.ErrorDeviceLost;
    }
}
