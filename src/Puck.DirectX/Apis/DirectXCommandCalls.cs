using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX.Apis;

/// <summary>Answers the Direct3D 12 calls a device removal reaches with their <c>HRESULT</c>: mapping a resource,
/// resetting and closing a command list, signalling a queue and waiting on a fence. A removed device fails each of
/// them with <c>DXGI_ERROR_DEVICE_REMOVED</c>, <c>_RESET</c> or <c>_HUNG</c>, which is an answer to translate, never an
/// exception from the generated wrapper.</summary>
public unsafe interface IDirectXCommandCalls {
    /// <summary>Asks <c>ID3D12Resource::Map</c> for subresource zero, reading nothing.</summary>
    /// <param name="resource">The resource mapped.</param>
    /// <param name="data">Receives the mapped pointer on success.</param>
    /// <returns>The call's result.</returns>
    HRESULT Map(ID3D12Resource* resource, void** data);
    /// <summary>Asks <c>ID3D12CommandAllocator::Reset</c>.</summary>
    /// <param name="allocator">The allocator reset.</param>
    /// <returns>The call's result.</returns>
    HRESULT ResetAllocator(ID3D12CommandAllocator* allocator);
    /// <summary>Asks <c>ID3D12GraphicsCommandList::Reset</c> with no initial pipeline state.</summary>
    /// <param name="commandList">The command list reset.</param>
    /// <param name="allocator">The allocator the list records into.</param>
    /// <returns>The call's result.</returns>
    HRESULT ResetList(ID3D12GraphicsCommandList* commandList, ID3D12CommandAllocator* allocator);
    /// <summary>Asks <c>ID3D12GraphicsCommandList::Close</c>.</summary>
    /// <param name="commandList">The command list closed.</param>
    /// <returns>The call's result.</returns>
    HRESULT Close(ID3D12GraphicsCommandList* commandList);
    /// <summary>Asks <c>ID3D12CommandQueue::Signal</c>.</summary>
    /// <param name="queue">The queue that signals.</param>
    /// <param name="fence">The fence signalled.</param>
    /// <param name="value">The value the fence is set to once the queue reaches the signal.</param>
    /// <returns>The call's result.</returns>
    HRESULT Signal(ID3D12CommandQueue* queue, ID3D12Fence* fence, ulong value);
    /// <summary>Reads <c>ID3D12Fence::GetCompletedValue</c>; a removed device reads <c>UINT64_MAX</c>.</summary>
    /// <param name="fence">The fence read.</param>
    /// <returns>The fence's completed value.</returns>
    ulong CompletedValue(ID3D12Fence* fence);
    /// <summary>Asks <c>ID3D12Fence::SetEventOnCompletion</c>.</summary>
    /// <param name="fence">The fence waited on.</param>
    /// <param name="value">The value the event waits for.</param>
    /// <param name="fenceEvent">The event set when the fence reaches <paramref name="value"/>.</param>
    /// <returns>The call's result.</returns>
    HRESULT SetEventOnCompletion(ID3D12Fence* fence, ulong value, HANDLE fenceEvent);
    /// <summary>Reads <c>ID3D12Device::GetDeviceRemovedReason</c>, the result that explains a removal
    /// (<c>DXGI_ERROR_DEVICE_HUNG</c> for a GPU timeout, <c>DXGI_ERROR_DRIVER_INTERNAL_ERROR</c> for invalid work or a
    /// page fault); <c>S_OK</c> while the device is healthy.</summary>
    /// <returns>The removal reason.</returns>
    HRESULT DeviceRemovedReason();
}
/// <summary>A device's own command calls, through each interface's vtable slot rather than the generated wrapper,
/// which throws a <c>COMException</c> the host's device-loss recovery never sees.</summary>
/// <param name="device">The device whose objects are called and whose removal reason is read; it stays owned by the
/// caller.</param>
[SupportedOSPlatform("windows10.0.10240")]
public readonly unsafe struct DirectXDeviceCommandCalls(ID3D12Device* device) : IDirectXCommandCalls {
    // The slots count IUnknown's three methods, ID3D12Object's four and ID3D12DeviceChild's one; a command list adds
    // ID3D12CommandList's GetType before its own methods.
    private const int AllocatorResetSlot = 8;
    private const int FenceCompletedValueSlot = 8;
    private const int FenceSetEventSlot = 9;
    private const int ListCloseSlot = 9;
    private const int ListResetSlot = 10;
    private const int QueueSignalSlot = 14;
    private const int ResourceMapSlot = 8;

    /// <summary>Creates the calls for a device context's current device.</summary>
    /// <param name="deviceContext">The context whose device is called.</param>
    /// <returns>The calls.</returns>
    public static DirectXDeviceCommandCalls Of(IDirectXDeviceContext deviceContext) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        return new DirectXDeviceCommandCalls(device: ((ID3D12Device*)deviceContext.Device.Handle));
    }

    private static void** VtableOf(void* instance) => *((void***)instance);

    /// <inheritdoc/>
    public HRESULT Map(ID3D12Resource* resource, void** data) =>
        ((delegate* unmanaged[Stdcall]<ID3D12Resource*, uint, D3D12_RANGE*, void**, HRESULT>)VtableOf(instance: resource)[ResourceMapSlot])(
            resource,
            0U,
            null,
            data
        );
    /// <inheritdoc/>
    public HRESULT ResetAllocator(ID3D12CommandAllocator* allocator) =>
        ((delegate* unmanaged[Stdcall]<ID3D12CommandAllocator*, HRESULT>)VtableOf(instance: allocator)[AllocatorResetSlot])(allocator);
    /// <inheritdoc/>
    public HRESULT ResetList(ID3D12GraphicsCommandList* commandList, ID3D12CommandAllocator* allocator) =>
        ((delegate* unmanaged[Stdcall]<ID3D12GraphicsCommandList*, ID3D12CommandAllocator*, ID3D12PipelineState*, HRESULT>)VtableOf(instance: commandList)[ListResetSlot])(
            commandList,
            allocator,
            null
        );
    /// <inheritdoc/>
    public HRESULT Close(ID3D12GraphicsCommandList* commandList) =>
        ((delegate* unmanaged[Stdcall]<ID3D12GraphicsCommandList*, HRESULT>)VtableOf(instance: commandList)[ListCloseSlot])(commandList);
    /// <inheritdoc/>
    public HRESULT Signal(ID3D12CommandQueue* queue, ID3D12Fence* fence, ulong value) =>
        ((delegate* unmanaged[Stdcall]<ID3D12CommandQueue*, ID3D12Fence*, ulong, HRESULT>)VtableOf(instance: queue)[QueueSignalSlot])(
            queue,
            fence,
            value
        );
    /// <inheritdoc/>
    public ulong CompletedValue(ID3D12Fence* fence) =>
        ((delegate* unmanaged[Stdcall]<ID3D12Fence*, ulong>)VtableOf(instance: fence)[FenceCompletedValueSlot])(fence);
    /// <inheritdoc/>
    public HRESULT SetEventOnCompletion(ID3D12Fence* fence, ulong value, HANDLE fenceEvent) =>
        ((delegate* unmanaged[Stdcall]<ID3D12Fence*, ulong, HANDLE, HRESULT>)VtableOf(instance: fence)[FenceSetEventSlot])(
            fence,
            value,
            fenceEvent
        );
    /// <inheritdoc/>
    public HRESULT DeviceRemovedReason() =>
        ((delegate* unmanaged[Stdcall]<ID3D12Device*, HRESULT>)VtableOf(instance: device)[DirectXConstants.GetDeviceRemovedReasonSlot])(device);
}
/// <summary>
/// The Direct3D 12 calls a device removal reaches, each through <see cref="IDirectXCommandCalls"/> and checked by
/// <see cref="HResultExtensions.ThrowIfFailed{TCalls}(HRESULT, TCalls, string)"/>: a removal result becomes
/// <see cref="DeviceLostException"/> carrying the device's removal reason, and any other failure a
/// <see cref="DirectXException"/>.
/// <para>A drain on the way to releasing objects is the one exception, by rule: <see cref="Drain"/> treats a removal as
/// drained. A removed device executes nothing further and its fences read complete, so no object a release frees is
/// still in use, and a release that threw would leave the objects held on the device whose teardown then names them as
/// leaked. Every release path drains through <see cref="Drain"/>; every frame path waits through
/// <see cref="SignalAndWait"/>, which throws so the host's recovery sees the loss.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXCommandCalls {
    /// <summary>Maps subresource zero of a resource for writing.</summary>
    /// <typeparam name="TCalls">The calls' answerer.</typeparam>
    /// <param name="calls">The calls.</param>
    /// <param name="resource">The resource mapped.</param>
    /// <returns>The mapped pointer.</returns>
    /// <exception cref="DeviceLostException">The device was removed.</exception>
    /// <exception cref="DirectXException">The map failed for another reason.</exception>
    public static void* Map<TCalls>(TCalls calls, ID3D12Resource* resource) where TCalls : IDirectXCommandCalls {
        void* mapped = null;

        calls.Map(
            data: &mapped,
            resource: resource
        ).ThrowIfFailed(
            calls: calls,
            operation: "ID3D12Resource::Map"
        );

        return mapped;
    }
    /// <summary>Resets an allocator and the command list that records into it, opening the list for recording.</summary>
    /// <typeparam name="TCalls">The calls' answerer.</typeparam>
    /// <param name="calls">The calls.</param>
    /// <param name="allocator">The allocator reset.</param>
    /// <param name="commandList">The command list reset onto <paramref name="allocator"/>.</param>
    /// <exception cref="DeviceLostException">The device was removed.</exception>
    /// <exception cref="DirectXException">A reset failed for another reason.</exception>
    public static void Reset<TCalls>(TCalls calls, ID3D12CommandAllocator* allocator, ID3D12GraphicsCommandList* commandList) where TCalls : IDirectXCommandCalls {
        calls.ResetAllocator(allocator: allocator).ThrowIfFailed(
            calls: calls,
            operation: "ID3D12CommandAllocator::Reset"
        );
        calls.ResetList(
            allocator: allocator,
            commandList: commandList
        ).ThrowIfFailed(
            calls: calls,
            operation: "ID3D12GraphicsCommandList::Reset"
        );
    }
    /// <summary>Closes a command list.</summary>
    /// <typeparam name="TCalls">The calls' answerer.</typeparam>
    /// <param name="calls">The calls.</param>
    /// <param name="commandList">The command list closed.</param>
    /// <exception cref="DeviceLostException">The device was removed.</exception>
    /// <exception cref="DirectXException">The close failed for another reason, such as an invalid recording.</exception>
    public static void Close<TCalls>(TCalls calls, ID3D12GraphicsCommandList* commandList) where TCalls : IDirectXCommandCalls =>
        calls.Close(commandList: commandList).ThrowIfFailed(
            calls: calls,
            operation: "ID3D12GraphicsCommandList::Close"
        );
    /// <summary>Queues a fence signal.</summary>
    /// <typeparam name="TCalls">The calls' answerer.</typeparam>
    /// <param name="calls">The calls.</param>
    /// <param name="queue">The queue that signals.</param>
    /// <param name="fence">The fence signalled.</param>
    /// <param name="value">The value the fence is set to.</param>
    /// <exception cref="DeviceLostException">The device was removed.</exception>
    /// <exception cref="DirectXException">The signal failed for another reason.</exception>
    public static void Signal<TCalls>(TCalls calls, ID3D12CommandQueue* queue, ID3D12Fence* fence, ulong value) where TCalls : IDirectXCommandCalls =>
        calls.Signal(
            fence: fence,
            queue: queue,
            value: value
        ).ThrowIfFailed(
            calls: calls,
            operation: "ID3D12CommandQueue::Signal"
        );
    /// <summary>Blocks until a fence reaches a value. A removed device's fence reads <c>UINT64_MAX</c>, so the wait
    /// returns at once.</summary>
    /// <typeparam name="TCalls">The calls' answerer.</typeparam>
    /// <param name="calls">The calls.</param>
    /// <param name="fence">The fence waited on.</param>
    /// <param name="fenceEvent">The event the wait blocks on.</param>
    /// <param name="value">The value waited for.</param>
    /// <exception cref="DeviceLostException">The device was removed.</exception>
    /// <exception cref="DirectXException">Arming the event failed for another reason.</exception>
    public static void Wait<TCalls>(TCalls calls, ID3D12Fence* fence, HANDLE fenceEvent, ulong value) where TCalls : IDirectXCommandCalls {
        if (calls.CompletedValue(fence: fence) >= value) {
            return;
        }

        calls.SetEventOnCompletion(
            fence: fence,
            fenceEvent: fenceEvent,
            value: value
        ).ThrowIfFailed(
            calls: calls,
            operation: "ID3D12Fence::SetEventOnCompletion"
        );
        _ = PInvoke.WaitForSingleObject(
            dwMilliseconds: uint.MaxValue,
            hHandle: fenceEvent
        );
    }
    /// <summary>Signals a fence on a queue and blocks until the queue reaches it: every submission before the call has
    /// completed when it returns. A frame path's wait.</summary>
    /// <typeparam name="TCalls">The calls' answerer.</typeparam>
    /// <param name="calls">The calls.</param>
    /// <param name="queue">The queue drained.</param>
    /// <param name="fence">The fence signalled.</param>
    /// <param name="fenceEvent">The event the wait blocks on.</param>
    /// <param name="fenceValue">The next value to signal; advanced by one.</param>
    /// <exception cref="DeviceLostException">The device was removed.</exception>
    /// <exception cref="DirectXException">A call failed for another reason.</exception>
    public static void SignalAndWait<TCalls>(TCalls calls, ID3D12CommandQueue* queue, ID3D12Fence* fence, HANDLE fenceEvent, ref ulong fenceValue) where TCalls : IDirectXCommandCalls {
        var value = fenceValue;

        Signal(
            calls: calls,
            fence: fence,
            queue: queue,
            value: value
        );
        fenceValue++;
        Wait(
            calls: calls,
            fence: fence,
            fenceEvent: fenceEvent,
            value: value
        );
    }
    /// <summary>Drains a queue before its objects are released: <see cref="SignalAndWait"/>, except that a removed
    /// device counts as drained (see the class remarks for the rule).</summary>
    /// <typeparam name="TCalls">The calls' answerer.</typeparam>
    /// <param name="calls">The calls.</param>
    /// <param name="queue">The queue drained.</param>
    /// <param name="fence">The fence signalled.</param>
    /// <param name="fenceEvent">The event the wait blocks on.</param>
    /// <param name="fenceValue">The next value to signal; advanced by one when the signal is queued.</param>
    /// <returns><see langword="false"/> when the device was removed and nothing was waited on.</returns>
    /// <exception cref="DirectXException">A call failed for a reason other than a removal.</exception>
    public static bool Drain<TCalls>(TCalls calls, ID3D12CommandQueue* queue, ID3D12Fence* fence, HANDLE fenceEvent, ref ulong fenceValue) where TCalls : IDirectXCommandCalls {
        var value = fenceValue;
        var signalled = calls.Signal(
            fence: fence,
            queue: queue,
            value: value
        );

        if (HResultExtensions.IsDeviceRemoval(result: signalled)) {
            return false;
        }

        signalled.ThrowIfFailed(
            calls: calls,
            operation: "ID3D12CommandQueue::Signal"
        );
        fenceValue++;

        if (calls.CompletedValue(fence: fence) >= value) {
            return true;
        }

        var armed = calls.SetEventOnCompletion(
            fence: fence,
            fenceEvent: fenceEvent,
            value: value
        );

        if (HResultExtensions.IsDeviceRemoval(result: armed)) {
            return false;
        }

        armed.ThrowIfFailed(
            calls: calls,
            operation: "ID3D12Fence::SetEventOnCompletion"
        );
        _ = PInvoke.WaitForSingleObject(
            dwMilliseconds: uint.MaxValue,
            hHandle: fenceEvent
        );

        return true;
    }
}
