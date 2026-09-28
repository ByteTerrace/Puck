using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// The one rule for an object that holds resources on a Vulkan logical device it does not own, such as a surface upload
/// or a shared-surface import: it stays on the device it first created them on, and its owner releases it before that
/// device goes, a device loss included, then creates a new one on the replacement. Destroying a device does not free
/// its children (<c>VUID-vkDestroyDevice-device-05137</c>), and destroying them afterwards is a use after free, so both
/// a different device and a release after destruction are refused by name rather than tolerated.
/// </summary>
public static class VulkanDeviceOwnership {
    /// <summary>Refuses a device other than the one the holder's resources are on.</summary>
    /// <param name="held">The device the holder's resources are on, or <see langword="null"/> before it holds any.</param>
    /// <param name="offered">The device the holder is asked to work on.</param>
    /// <param name="holder">The holder's type name, which the refusal names.</param>
    /// <exception cref="ArgumentNullException"><paramref name="offered"/> or <paramref name="holder"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="held"/> is a different device than
    /// <paramref name="offered"/>.</exception>
    public static void ThrowIfOtherDevice(VulkanLogicalDevice? held, VulkanLogicalDevice offered, string holder) {
        ArgumentNullException.ThrowIfNull(argument: offered);
        ArgumentNullException.ThrowIfNull(argument: holder);

        if (
            (held is not null) &&
            (held.Commands != offered.Commands)
        ) {
            throw new InvalidOperationException(message: $"A {holder} was handed a device other than the one it holds resources on; its owner must release it before its device goes and create a new one on the replacement.");
        }
    }
    /// <summary>Refuses to release a holder's resources after their device was destroyed.</summary>
    /// <param name="held">The device the holder's resources are on.</param>
    /// <param name="holder">The holder's type name, which the refusal names.</param>
    /// <exception cref="ArgumentNullException"><paramref name="held"/> or <paramref name="holder"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="held"/> is destroyed, so the owner disposing the
    /// holder released it after its device.</exception>
    public static void ThrowIfDestroyed(VulkanLogicalDevice held, string holder) {
        ArgumentNullException.ThrowIfNull(argument: held);
        ArgumentNullException.ThrowIfNull(argument: holder);

        if (held.IsDisposed) {
            throw new InvalidOperationException(message: $"A {holder} was released after its device was destroyed; the owner disposing it must release it before the device goes.");
        }
    }
}
