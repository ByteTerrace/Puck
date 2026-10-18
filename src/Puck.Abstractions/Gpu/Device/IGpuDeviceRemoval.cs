namespace Puck.Abstractions.Gpu;

/// <summary>
/// A device context that can remove its own device on purpose, as a hang or fault would, so its device-removal
/// diagnostics and the host's recovery can be exercised on demand (<c>gpu.faults remove</c>). Direct3D 12 implements it
/// through <c>ID3D12Device5::RemoveDevice</c>; Vulkan has no such call.
/// </summary>
public interface IGpuDeviceRemoval {
    /// <summary>Removes the current device: every later call on it reports a removed device.</summary>
    /// <returns><see langword="true"/> when a device was removed; <see langword="false"/> when there is none to remove,
    /// or the runtime cannot remove it.</returns>
    bool RemoveDevice();
}
