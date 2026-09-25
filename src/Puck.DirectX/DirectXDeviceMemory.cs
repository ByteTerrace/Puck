using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Counts the backend's resources into its <c>memory.directx</c> <see cref="GpuDeviceMemoryWork"/> by their
/// <see cref="GpuMemoryRole"/>, each at the size <c>ID3D12Device::GetResourceAllocationInfo</c> reports for its
/// description: a <c>DEFAULT</c>-heap buffer or texture, exported textures included, and a resource opened from a shared
/// handle are <see cref="GpuMemoryRole.DeviceLocal"/>; an <c>UPLOAD</c> or <c>READBACK</c> buffer is
/// <see cref="GpuMemoryRole.HostVisible"/>. Swapchain buffers, which DXGI allocates, never reach here. Each entry is
/// keyed by the device the resource reports (<c>ID3D12DeviceChild::GetDevice</c>), so a release after the context
/// recreated its device still reaches the entry the old device made. An owner counts the release with
/// <see cref="CountReleased"/> just before its last reference goes.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXDeviceMemory {
    // ID3D12Device::GetResourceAllocationInfo sits twelve slots before GetDeviceRemovedReason (37), and
    // ID3D12Resource::GetDesc follows IUnknown, ID3D12Object, ID3D12DeviceChild::GetDevice, Map and Unmap. Both return
    // a struct by value through the hidden-pointer x64 COM ABI the CsWin32 wrapper omits, so they are invoked through
    // their slots with the return-by-pointer signature. ID3D12DeviceChild::GetDevice follows IUnknown's three slots and
    // ID3D12Object's four.
    private const int GetResourceAllocationInfoSlot = 25;
    private const int GetResourceDescSlot = 10;
    private const int GetDeviceSlot = 7;

    // The ID3D12Device a resource was made on, as the pointer the device was created through: a device is created as
    // ID3D12Device, and its interfaces share one pointer. The reference GetDevice adds is released at once.
    private static nint DeviceOf(ID3D12Resource* resource) {
        var deviceIid = ID3D12Device.IID_Guid;
        void* device = null;
        var result = ((delegate* unmanaged[Stdcall]<ID3D12Resource*, Guid*, void**, int>)(*((void***)resource))[GetDeviceSlot])(
            resource,
            &deviceIid,
            &device
        );

        if (
            (result < 0) ||
            (device is null)
        ) {
            throw new DirectXException(
                operation: "ID3D12DeviceChild::GetDevice",
                result: result
            );
        }

        _ = ((ID3D12Device*)device)->Release();

        return ((nint)device);
    }

    /// <summary>Counts a resource created or opened on a device when its role counts
    /// (<see cref="GpuDeviceMemoryWork.IsCounted"/>).</summary>
    /// <param name="memory">The counts, or <see langword="null"/> to count nothing.</param>
    /// <param name="device">The device that created or opened the resource.</param>
    /// <param name="resource">The resource.</param>
    /// <param name="role">What the resource is for.</param>
    public static void CountAllocated(GpuDeviceMemoryWork? memory, ID3D12Device* device, ID3D12Resource* resource, GpuMemoryRole role) {
        if (
            (memory is null) ||
            (resource is null) ||
            !GpuDeviceMemoryWork.IsCounted(role: role)
        ) {
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
        _ = memory.CountAllocated(
            allocation: ((nint)resource),
            bytes: checked((long)allocation.SizeInBytes),
            device: DeviceOf(resource: resource),
            role: role
        );
    }
    /// <summary>Counts the release of a resource <see cref="CountAllocated"/> counted; any other resource counts
    /// nothing.</summary>
    /// <param name="memory">The counts, or <see langword="null"/> to count nothing.</param>
    /// <param name="resource">The resource about to be released, or zero.</param>
    public static void CountReleased(GpuDeviceMemoryWork? memory, nint resource) {
        if (
            (memory is null) ||
            (0 == resource)
        ) {
            return;
        }

        _ = memory.CountReleased(
            allocation: resource,
            device: DeviceOf(resource: ((ID3D12Resource*)resource))
        );
    }
}
