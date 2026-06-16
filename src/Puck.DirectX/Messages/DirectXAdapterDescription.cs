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
);
