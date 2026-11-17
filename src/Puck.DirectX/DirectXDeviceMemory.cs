using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Counts the backend's resources into its <c>memory.directx</c> <see cref="GpuDeviceMemoryWork"/> by their
/// <see cref="GpuMemoryRole"/>, each at the size <c>ID3D12Device::GetResourceAllocationInfo</c> reports for its
/// description: a <c>DEFAULT</c>-heap buffer or texture, exported textures included, and a resource opened from a shared
/// handle are <see cref="GpuMemoryRole.DeviceLocal"/>; a <c>GPU_UPLOAD</c> buffer is
/// <see cref="GpuMemoryRole.HostVisibleDeviceLocal"/>; an <c>UPLOAD</c> or <c>READBACK</c> buffer is
/// <see cref="GpuMemoryRole.HostVisible"/>. Swapchain buffers, which DXGI allocates, never reach here. Each entry is
/// keyed by the device the resource reports (<c>ID3D12DeviceChild::GetDevice</c>), so a release after the context
/// recreated its device still reaches the entry the old device made. An owner counts the release with
/// <see cref="CountReleased"/> just before its last reference goes.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXDeviceMemory {
    // ID3D12Resource::GetDesc follows IUnknown, ID3D12Object, ID3D12DeviceChild::GetDevice, Map and Unmap. It returns a
    // struct by value through the hidden-pointer x64 COM ABI the CsWin32 wrapper omits, so it is invoked through its slot
    // with the return-by-pointer signature. ID3D12DeviceChild::GetDevice follows IUnknown's three slots and
    // ID3D12Object's four.
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

    /// <summary>Returns the segment a heap's memory lies in on a device's adapter: the segment group of the heap's memory
    /// pool, <c>D3D12_MEMORY_POOL_L1</c> the local group and <c>D3D12_MEMORY_POOL_L0</c> the non-local one on a discrete
    /// adapter, and the local group, the only one, on a unified adapter. A standard heap type's pool is the one
    /// <c>ID3D12Device::GetCustomHeapProperties</c> reports for it on the adapter; where it reports none, a
    /// <c>DEFAULT</c> or <c>GPU_UPLOAD</c> heap is local and any other non-local. This is the one statement of a Direct3D
    /// 12 allocation's placement, for a committed resource as for a placed one's heap.</summary>
    /// <typeparam name="TCalls">The type of the device calls.</typeparam>
    /// <param name="calls">The device's calls.</param>
    /// <param name="segments">The device's segments.</param>
    /// <param name="heap">The heap's properties: its type, and for a custom heap its memory pool.</param>
    /// <returns>The segment's index (<see cref="GpuMemorySegments.DirectXLocal"/> or
    /// <see cref="GpuMemorySegments.DirectXNonLocal"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> is <see langword="null"/>.</exception>
    public static int SegmentOf<TCalls>(TCalls calls, GpuMemorySegments segments, in D3D12_HEAP_PROPERTIES heap) where TCalls : IDirectXCommandCalls {
        ArgumentNullException.ThrowIfNull(argument: segments);

        if (segments.UnifiedMemory) {
            return GpuMemorySegments.DirectXLocal;
        }

        var pool = ((heap.Type == D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_CUSTOM)
            ? heap.MemoryPoolPreference
            : calls.CustomHeapProperties(heapType: heap.Type).MemoryPoolPreference);

        return pool switch {
            D3D12_MEMORY_POOL.D3D12_MEMORY_POOL_L1 => GpuMemorySegments.DirectXLocal,
            D3D12_MEMORY_POOL.D3D12_MEMORY_POOL_L0 => GpuMemorySegments.DirectXNonLocal,
            _ => ((heap.Type is D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT or D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_GPU_UPLOAD)
                ? GpuMemorySegments.DirectXLocal
                : GpuMemorySegments.DirectXNonLocal),
        };
    }
    /// <summary>The <c>HRESULT</c> a creation answers when the device has no memory for it (<c>E_OUTOFMEMORY</c>).</summary>
    public const int OutOfMemory = unchecked((int)0x8007000E);

    /// <summary>Asks the ledger whether a committed resource may be created, before it is: at the size the device
    /// reports for its description, in the segment its heap lies in (<see cref="SegmentOf"/>).</summary>
    /// <typeparam name="TCalls">The calls' answerer.</typeparam>
    /// <param name="memory">The ledger, or <see langword="null"/> to ask nothing.</param>
    /// <param name="calls">The device's calls.</param>
    /// <param name="device">The device that will create the resource; registered with <paramref name="memory"/>.</param>
    /// <param name="description">The resource's description.</param>
    /// <param name="heap">The properties of the heap it will be committed in.</param>
    /// <param name="role">What the resource is for.</param>
    /// <param name="family">The family the resource's memory is attributed to (<see cref="GpuObjectName.MemoryFamily"/>),
    /// which names the whole committed allocation.</param>
    /// <returns>The grant, which <see cref="CountAllocated(GpuDeviceMemoryWork?, GpuMemoryGrant?, ID3D12Resource*)"/>
    /// takes once the resource exists; <see langword="null"/> when <paramref name="memory"/> is.</returns>
    /// <exception cref="GpuMemoryRefusedException">The account refused the allocation; nothing was created.</exception>
    public static GpuMemoryGrant? Admit<TCalls>(GpuDeviceMemoryWork? memory, TCalls calls, ID3D12Device* device, in D3D12_RESOURCE_DESC description, in D3D12_HEAP_PROPERTIES heap, GpuMemoryRole role, string family) where TCalls : IDirectXCommandCalls {
        if (memory is null) {
            return null;
        }

        var sized = description;
        var bytes = checked((long)calls.AllocationInfo(description: &sized).SizeInBytes);

        return memory.Admit(
            bytes: bytes,
            device: ((nint)device),
            family: family,
            payer: family,
            payerBytes: bytes,
            role: role,
            segment: SegmentOf(calls: calls, heap: in heap, segments: memory.SegmentsOf(device: ((nint)device)))
        );
    }
    /// <summary>Settles a grant against the result of the native creation it was made for: a creation the device
    /// refused for want of memory hands the grant back as a device refusal, any other failure returns it, and both
    /// throw; a success leaves the grant for <see cref="GpuDeviceMemoryWork.CountAllocated"/>.</summary>
    /// <typeparam name="TCalls">The calls' answerer.</typeparam>
    /// <param name="calls">The device's calls.</param>
    /// <param name="grant">The grant, or <see langword="null"/> when nothing was asked.</param>
    /// <param name="result">The creation's result.</param>
    /// <param name="operation">The native operation, for the failure's message.</param>
    /// <exception cref="GpuMemoryRefusedException">The device refused the creation for want of memory.</exception>
    /// <exception cref="DeviceLostException">The device was removed.</exception>
    /// <exception cref="DirectXException">The creation failed for another reason.</exception>
    public static void Settle<TCalls>(TCalls calls, GpuMemoryGrant? grant, HRESULT result, string operation) where TCalls : IDirectXCommandCalls {
        if (!result.Failed) {
            return;
        }
        if ((result.Value == OutOfMemory) && (grant is not null)) {
            throw grant.Refuse();
        }

        grant?.Return();
        result.ThrowIfFailed(calls: calls, operation: operation);
    }
    /// <summary>Counts a committed resource created under a grant (<see cref="Admit"/>).</summary>
    /// <param name="memory">The ledger, or <see langword="null"/> to count nothing.</param>
    /// <param name="grant">The grant the resource was created under, or <see langword="null"/> when nothing was asked.</param>
    /// <param name="resource">The resource.</param>
    public static void CountAllocated(GpuDeviceMemoryWork? memory, GpuMemoryGrant? grant, ID3D12Resource* resource) {
        if ((memory is not null) && (grant is not null)) { memory.CountAllocated(allocation: ((nint)resource), grant: grant); }
    }
    /// <summary>Counts a resource opened on a device from a handle another device or process shared: its memory exists
    /// already, so it is admitted as it is opened, at the size the device reports for it, in the role row its role names
    /// (<see cref="GpuDeviceMemoryWork.IsDeviceLocal"/>) and the segment its heap lies in (<see cref="SegmentOf"/>), read
    /// from the resource itself (<c>ID3D12Resource::GetHeapProperties</c>).</summary>
    /// <param name="memory">The counts, or <see langword="null"/> to count nothing.</param>
    /// <param name="device">The device that opened the resource; registered with <paramref name="memory"/>.</param>
    /// <param name="resource">The resource.</param>
    /// <param name="role">What the resource is for.</param>
    /// <param name="family">The family the resource's memory is attributed to (<see cref="GpuObjectName.MemoryFamily"/>),
    /// which names the whole allocation.</param>
    /// <exception cref="InvalidOperationException">The device registered no segments with <paramref name="memory"/>.</exception>
    /// <exception cref="GpuMemoryRefusedException">The account refused the opened memory; the caller releases the
    /// resource.</exception>
    public static void CountOpened(GpuDeviceMemoryWork? memory, ID3D12Device* device, ID3D12Resource* resource, GpuMemoryRole role, string family) {
        if ((memory is null) || (resource is null)) {
            return;
        }

        var resourceVtable = *((void***)resource);
        D3D12_RESOURCE_DESC description;

        ((delegate* unmanaged[Stdcall]<ID3D12Resource*, D3D12_RESOURCE_DESC*, D3D12_RESOURCE_DESC*>)resourceVtable[GetResourceDescSlot])(
            resource,
            &description
        );

        var calls = new DirectXDeviceCommandCalls(device: device);
        var allocation = calls.AllocationInfo(description: &description);
        var owner = DeviceOf(resource: resource);
        var heap = default(D3D12_HEAP_PROPERTIES);
        var flags = default(D3D12_HEAP_FLAGS);

        resource->GetHeapProperties(&heap, &flags);
        memory.CountAllocated(
            allocation: ((nint)resource),
            grant: memory.Admit(
                bytes: checked((long)allocation.SizeInBytes),
                device: owner,
                family: family,
                payer: family,
                payerBytes: checked((long)allocation.SizeInBytes),
                role: role,
                segment: SegmentOf(calls: calls, heap: in heap, segments: memory.SegmentsOf(device: owner))
            )
        );
    }
    /// <summary>Counts the release of a resource <see cref="CountAllocated"/> counted; any other resource counts
    /// nothing.</summary>
    /// <param name="memory">The counts, or <see langword="null"/> to count nothing.</param>
    /// <param name="resource">The resource about to be released, or zero.</param>
    public static void CountReleased(GpuDeviceMemoryWork? memory, nint resource) {
        if (0 != resource) {
            DirectXShaderVisibleHeaps.ForgetClears(resource: resource);
        }
        // A placed resource returns its range to its heap, which alone the counts hold, and ends its family's name there.
        if ((0 != resource) && DirectXHeapArena.Release(resource: resource)) {
            return;
        }
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
