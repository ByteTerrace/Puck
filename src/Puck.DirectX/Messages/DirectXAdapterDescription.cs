namespace Puck.DirectX.Messages;

/// <summary>
/// A condensed, managed description of a DXGI graphics adapter, projected from <c>DXGI_ADAPTER_DESC1</c>.
/// </summary>
/// <param name="AdapterLuid">The adapter's locally unique identifier, packed as <c>(HighPart &lt;&lt; 32) | LowPart</c>; stable for the life of the machine boot and usable to re-locate the adapter.</param>
/// <param name="DedicatedSystemMemory">The number of bytes of dedicated system memory not shared with the CPU.</param>
/// <param name="DedicatedVideoMemory">The number of bytes of dedicated video memory not shared with the CPU.</param>
/// <param name="Description">The human-readable adapter description (for example, the GPU model name).</param>
/// <param name="DeviceId">The PCI device identifier of the hardware.</param>
/// <param name="IsSoftware">Whether the adapter is a software (non-hardware) renderer, such as WARP.</param>
/// <param name="Revision">The PCI revision number of the adapter.</param>
/// <param name="SharedSystemMemory">The maximum number of bytes of system memory the adapter may consume.</param>
/// <param name="SubSystemId">The PCI subsystem identifier of the hardware.</param>
/// <param name="VendorId">The PCI vendor identifier of the hardware.</param>
public readonly record struct DirectXAdapterDescription(
    long AdapterLuid,
    ulong DedicatedSystemMemory,
    ulong DedicatedVideoMemory,
    string Description,
    uint DeviceId,
    bool IsSoftware,
    uint Revision,
    ulong SharedSystemMemory,
    uint SubSystemId,
    uint VendorId
) {
    /// <summary>Gets whether a device created for <paramref name="adapterLuid"/> is created on this adapter: the adapter
    /// carrying that LUID, or, for zero, any hardware adapter and never a software renderer. A process Windows no longer
    /// lets use the GPU, after repeated device faults, enumerates only the Microsoft Basic Render Driver, so the first
    /// adapter alone would move a device law or a World onto the CPU rather than refuse.</summary>
    /// <param name="adapterLuid">The requested packed LUID, or zero for the first hardware adapter.</param>
    /// <returns>Whether the request selects this adapter.</returns>
    public bool IsSelectedBy(long adapterLuid) => ((0L == adapterLuid) ? !IsSoftware : (AdapterLuid == adapterLuid));
}
