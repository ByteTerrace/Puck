using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Counts the backend's device-local resources into its <c>memory.directx</c> <see cref="GpuDeviceMemoryWork"/>: every
/// committed buffer and texture on a <c>DEFAULT</c> heap, exported textures included, and every resource opened from a
/// shared handle, each at the size <c>ID3D12Device::GetResourceAllocationInfo</c> reports for its description. Upload
/// and readback buffers are host memory and are not counted, and swapchain buffers, which DXGI allocates, never reach
/// here. An owner counts the release with <see cref="CountReleased"/> just before its last reference goes.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXDeviceMemory {
    // ID3D12Device::GetResourceAllocationInfo sits twelve slots before GetDeviceRemovedReason (37), and
    // ID3D12Resource::GetDesc follows IUnknown, ID3D12Object, ID3D12DeviceChild::GetDevice, Map and Unmap. Both return
    // a struct by value through the hidden-pointer x64 COM ABI the CsWin32 wrapper omits, so they are invoked through
    // their slots with the return-by-pointer signature.
    private const int GetResourceAllocationInfoSlot = 25;
    private const int GetResourceDescSlot = 10;

    /// <summary>Counts a resource created on a <c>DEFAULT</c> heap or opened from a shared handle.</summary>
    /// <param name="memory">The counts, or <see langword="null"/> to count nothing.</param>
    /// <param name="device">The device that created or opened the resource.</param>
    /// <param name="resource">The resource.</param>
    public static void CountAllocated(GpuDeviceMemoryWork? memory, ID3D12Device* device, ID3D12Resource* resource) {
        if ((memory is null) || (resource is null)) {
            return;
        }

        var resourceVtable = *((void***)resource);
        D3D12_RESOURCE_DESC description;

        ((delegate* unmanaged[Stdcall]<ID3D12Resource*, D3D12_RESOURCE_DESC*, D3D12_RESOURCE_DESC*>)resourceVtable[GetResourceDescSlot])(
            resource,
            &description
        );

        var deviceVtable = *((void***)device);
        D3D12_RESOURCE_ALLOCATION_INFO allocation;

        ((delegate* unmanaged[Stdcall]<ID3D12Device*, D3D12_RESOURCE_ALLOCATION_INFO*, uint, uint, D3D12_RESOURCE_DESC*, D3D12_RESOURCE_ALLOCATION_INFO*>)deviceVtable[GetResourceAllocationInfoSlot])(
            device,
            &allocation,
            0U,
            1U,
            &description
        );
        memory.CountAllocated(
            allocation: ((nint)resource),
            bytes: checked((long)allocation.SizeInBytes)
        );
    }
    /// <summary>Counts the release of a resource <see cref="CountAllocated"/> counted; any other resource counts
    /// nothing.</summary>
    /// <param name="memory">The counts, or <see langword="null"/> to count nothing.</param>
    /// <param name="resource">The resource about to be released.</param>
    public static void CountReleased(GpuDeviceMemoryWork? memory, nint resource) =>
        _ = memory?.CountReleased(allocation: resource);
}
