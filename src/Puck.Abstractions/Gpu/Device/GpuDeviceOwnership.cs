namespace Puck.Abstractions.Gpu;

/// <summary>
/// A native device as a holder of resources on it sees it: one object per device a backend brings up (a Vulkan logical
/// device, a Direct3D 12 device), which a device context replaces with another object when it recreates its device
/// after a loss.
/// </summary>
public interface IGpuHeldDevice {
    /// <summary>Gets whether the device has been destroyed, so the children made on it can no longer be released.</summary>
    bool IsDisposed { get; }
}
/// <summary>
/// The one rule for an object that holds resources on a device it does not own, such as a surface upload, a readback or
/// a shared-surface import: it stays on the device it first created them on, and its owner releases it before that
/// device goes, a device loss included, then creates a new one on the replacement. A device context replaces its device
/// in place on a loss, so a holder that outlives the loss would see a different device through the same context; that,
/// and a release after the held device was destroyed, are refused by name rather than tolerated, since the holder's
/// objects are children the old device's teardown names as leaked (Vulkan: destroying them afterwards is a use after
/// free, <c>VUID-vkDestroyDevice-device-05137</c>).
/// </summary>
public static class GpuDeviceOwnership {
    /// <summary>Refuses a device other than the one the holder's resources are on.</summary>
    /// <param name="held">The device the holder's resources are on, or <see langword="null"/> before it holds any.</param>
    /// <param name="offered">The device the holder is asked to work on.</param>
    /// <param name="holder">The holder's type name, which the refusal names.</param>
    /// <exception cref="ArgumentNullException"><paramref name="offered"/> or <paramref name="holder"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="held"/> is a different device than
    /// <paramref name="offered"/>.</exception>
    public static void ThrowIfOtherDevice(IGpuHeldDevice? held, IGpuHeldDevice offered, string holder) {
        ArgumentNullException.ThrowIfNull(argument: offered);
        ArgumentNullException.ThrowIfNull(argument: holder);

        if (
            (held is not null) &&
            !ReferenceEquals(
                objA: held,
                objB: offered
            )
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
    public static void ThrowIfDestroyed(IGpuHeldDevice held, string holder) {
        ArgumentNullException.ThrowIfNull(argument: held);
        ArgumentNullException.ThrowIfNull(argument: holder);

        if (held.IsDisposed) {
            throw new InvalidOperationException(message: $"A {holder} was released after its device was destroyed; the owner disposing it must release it before the device goes.");
        }
    }
}
