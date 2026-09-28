using Puck.DirectX.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX.Apis;

public unsafe partial interface IDirectXCommandCalls {
    /// <summary>Asks the direct queue for its timestamp frequency.</summary>
    /// <param name="queue">The direct command queue.</param>
    /// <param name="frequency">Receives ticks per second on success.</param>
    /// <returns>The native result.</returns>
    HRESULT TimestampFrequency(ID3D12CommandQueue* queue, ulong* frequency);
    /// <summary>Creates a timestamp query heap.</summary>
    /// <param name="description">The timestamp heap description.</param>
    /// <param name="heap">Receives the owned heap on success.</param>
    /// <returns>The native result.</returns>
    HRESULT CreateQueryHeap(D3D12_QUERY_HEAP_DESC* description, ID3D12QueryHeap** heap);
}
public readonly unsafe partial struct DirectXDeviceCommandCalls {
    private const int QueueTimestampFrequencySlot = 16;
    private const int DeviceCreateQueryHeapSlot = 39;

    /// <inheritdoc/>
    public HRESULT TimestampFrequency(ID3D12CommandQueue* queue, ulong* frequency) =>
        ((delegate* unmanaged[Stdcall]<ID3D12CommandQueue*, ulong*, HRESULT>)VtableOf(instance: queue)[QueueTimestampFrequencySlot])(queue, frequency);
    /// <inheritdoc/>
    public HRESULT CreateQueryHeap(D3D12_QUERY_HEAP_DESC* description, ID3D12QueryHeap** heap) {
        var iid = ID3D12QueryHeap.IID_Guid;

        return ((delegate* unmanaged[Stdcall]<ID3D12Device*, D3D12_QUERY_HEAP_DESC*, Guid*, void**, HRESULT>)VtableOf(instance: device)[DeviceCreateQueryHeapSlot])(
            device, description, &iid, ((void**)heap));
    }
}
public static unsafe partial class DirectXCommandCalls {
    /// <summary>Creates a timestamp heap after reading the queue's frequency, translating native failures through
    /// the same device-loss boundary as every command-resource allocation.</summary>
    /// <typeparam name="TCalls">The native calls' answerer.</typeparam>
    /// <param name="calls">The device's native calls.</param>
    /// <param name="queue">The direct command queue.</param>
    /// <param name="count">The positive query count.</param>
    /// <param name="frequency">Receives ticks per second, or zero when timestamps are unavailable.</param>
    /// <returns>The owned heap, or null when the queue has no timestamp frequency.</returns>
    /// <exception cref="DeviceLostException">The device was removed.</exception>
    /// <exception cref="DirectXException">A native call failed for another reason.</exception>
    public static ID3D12QueryHeap* CreateTimestampHeap<TCalls>(TCalls calls, ID3D12CommandQueue* queue, uint count, out ulong frequency) where TCalls : IDirectXCommandCalls {
        ArgumentOutOfRangeException.ThrowIfZero(value: count);
        var ticks = 0UL;

        calls.TimestampFrequency(frequency: &ticks, queue: queue).ThrowIfFailed(calls: calls, operation: "ID3D12CommandQueue::GetTimestampFrequency");
        frequency = ticks;
        if (ticks == 0) { return null; }
        var description = new D3D12_QUERY_HEAP_DESC { Count = count, Type = D3D12_QUERY_HEAP_TYPE.D3D12_QUERY_HEAP_TYPE_TIMESTAMP };
        ID3D12QueryHeap* heap = null;

        calls.CreateQueryHeap(description: &description, heap: &heap).ThrowIfFailed(calls: calls, operation: "ID3D12Device::CreateQueryHeap");
        return heap;
    }
}
