using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Apis;

// The calls a heap arena makes (DirectXHeapArena): sizing a resource, creating a heap and a resource placed in it, tagging
// and reading a resource's token, and releasing a heap or a resource.
public unsafe partial interface IDirectXCommandCalls {
    /// <summary>Asks <c>ID3D12Device::GetResourceAllocationInfo</c> for one resource on node zero.</summary>
    /// <param name="description">The resource's description.</param>
    /// <returns>Its size and alignment in a heap.</returns>
    D3D12_RESOURCE_ALLOCATION_INFO AllocationInfo(D3D12_RESOURCE_DESC* description);
    /// <summary>Asks <c>ID3D12Device::CreateHeap</c>.</summary>
    /// <param name="description">The heap's description.</param>
    /// <param name="heap">Receives the heap on success, owned by the caller.</param>
    /// <returns>The call's result.</returns>
    HRESULT CreateHeap(D3D12_HEAP_DESC* description, ID3D12Heap** heap);
    /// <summary>Asks <c>ID3D12Device::CreatePlacedResource</c>.</summary>
    /// <param name="heap">The heap the resource is placed in.</param>
    /// <param name="offset">Its offset in the heap, aligned as its allocation info says.</param>
    /// <param name="description">The resource's description.</param>
    /// <param name="initialState">The state it is created in.</param>
    /// <param name="clearValue">The optimized clear value, or <see langword="null"/>.</param>
    /// <param name="resource">Receives the resource on success, owned by the caller.</param>
    /// <returns>The call's result.</returns>
    HRESULT CreatePlacedResource(ID3D12Heap* heap, ulong offset, D3D12_RESOURCE_DESC* description, D3D12_RESOURCE_STATES initialState, D3D12_CLEAR_VALUE* clearValue, ID3D12Resource** resource);
    /// <summary>Releases the caller's reference to a heap.</summary>
    /// <param name="heap">The heap.</param>
    void ReleaseHeap(ID3D12Heap* heap);
    /// <summary>Releases the caller's reference to a resource.</summary>
    /// <param name="resource">The resource.</param>
    void ReleaseResource(ID3D12Resource* resource);
    /// <summary>Attaches a value to a resource under a key (<c>ID3D12Object::SetPrivateData</c>), which travels with the
    /// resource itself.</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="key">The value's key.</param>
    /// <param name="value">The value.</param>
    /// <returns>The call's result.</returns>
    HRESULT Tag(ID3D12Resource* resource, Guid key, long value);
    /// <summary>Reads the value attached to a resource under a key (<c>ID3D12Object::GetPrivateData</c>).</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="key">The value's key.</param>
    /// <param name="value">The value, or zero.</param>
    /// <returns>Whether the resource holds a value under the key.</returns>
    bool TryReadTag(ID3D12Resource* resource, Guid key, out long value);
}
public readonly unsafe partial struct DirectXDeviceCommandCalls {
    // ID3D12Device's slots, counted as CreateCommittedResource's (27) is.
    private const int DeviceGetResourceAllocationInfoSlot = 25;
    private const int DeviceCreateHeapSlot = 28;
    private const int DeviceCreatePlacedResourceSlot = 29;
    // ID3D12Object's slots, after IUnknown's three.
    private const int ObjectGetPrivateDataSlot = 3;
    private const int ObjectSetPrivateDataSlot = 4;

    /// <inheritdoc/>
    public D3D12_RESOURCE_ALLOCATION_INFO AllocationInfo(D3D12_RESOURCE_DESC* description) {
        D3D12_RESOURCE_ALLOCATION_INFO info;

        ((delegate* unmanaged[Stdcall]<ID3D12Device*, D3D12_RESOURCE_ALLOCATION_INFO*, uint, uint, D3D12_RESOURCE_DESC*, D3D12_RESOURCE_ALLOCATION_INFO*>)VtableOf(instance: device)[DeviceGetResourceAllocationInfoSlot])(
            device,
            &info,
            0U,
            1U,
            description
        );

        return info;
    }
    /// <inheritdoc/>
    public HRESULT CreateHeap(D3D12_HEAP_DESC* description, ID3D12Heap** heap) {
        var iid = ID3D12Heap.IID_Guid;

        return ((delegate* unmanaged[Stdcall]<ID3D12Device*, D3D12_HEAP_DESC*, Guid*, void**, HRESULT>)VtableOf(instance: device)[DeviceCreateHeapSlot])(
            device,
            description,
            &iid,
            ((void**)heap)
        );
    }
    /// <inheritdoc/>
    public HRESULT CreatePlacedResource(ID3D12Heap* heap, ulong offset, D3D12_RESOURCE_DESC* description, D3D12_RESOURCE_STATES initialState, D3D12_CLEAR_VALUE* clearValue, ID3D12Resource** resource) {
        var iid = ID3D12Resource.IID_Guid;

        return ((delegate* unmanaged[Stdcall]<ID3D12Device*, ID3D12Heap*, ulong, D3D12_RESOURCE_DESC*, D3D12_RESOURCE_STATES, D3D12_CLEAR_VALUE*, Guid*, void**, HRESULT>)VtableOf(instance: device)[DeviceCreatePlacedResourceSlot])(
            device,
            heap,
            offset,
            description,
            initialState,
            clearValue,
            &iid,
            ((void**)resource)
        );
    }
    /// <inheritdoc/>
    public void ReleaseHeap(ID3D12Heap* heap) => _ = ((IUnknown*)heap)->Release();
    /// <inheritdoc/>
    public void ReleaseResource(ID3D12Resource* resource) => _ = ((IUnknown*)resource)->Release();
    /// <inheritdoc/>
    public HRESULT Tag(ID3D12Resource* resource, Guid key, long value) =>
        ((delegate* unmanaged[Stdcall]<ID3D12Resource*, Guid*, uint, void*, HRESULT>)VtableOf(instance: resource)[ObjectSetPrivateDataSlot])(
            resource,
            &key,
            ((uint)sizeof(long)),
            &value
        );
    /// <inheritdoc/>
    public bool TryReadTag(ID3D12Resource* resource, Guid key, out long value) {
        var size = ((uint)sizeof(long));
        long read = 0;
        var result = ((delegate* unmanaged[Stdcall]<ID3D12Resource*, Guid*, uint*, void*, HRESULT>)VtableOf(instance: resource)[ObjectGetPrivateDataSlot])(
            resource,
            &key,
            &size,
            &read
        );

        value = (result.Succeeded ? read : 0L);

        return result.Succeeded;
    }
}
