using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Security;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuQueueSubmitter"/> for Direct3D 12. Each <c>commandBufferHandle</c> in the span is
/// a GCHandle token pointing to a <see cref="DirectXCommandBufferState"/>; the underlying command list is
/// extracted and passed to <c>ExecuteCommandLists</c>. <see cref="SubmitAndWait"/> additionally calls
/// <see cref="IGpuDeviceContext.WaitIdle"/> on the device context. A submission fence is an
/// <c>ID3D12Fence</c> + event pair signaled on the queue right after the fenced execute. An external wait is an
/// <c>ID3D12CommandQueue::Wait</c> issued immediately before the next submission's execute, so that submission and every
/// later one on the queue wait for the shared fence on the GPU.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuQueueSubmitter(DirectXDeviceContext deviceContext) : IGpuQueueSubmitter {
    private readonly List<GpuExternalWait> m_externalWaits = [];

    /// <inheritdoc/>
    /// <remarks>Takes a <see cref="DirectXSharedFence"/> or a <see cref="DirectXExportableFence"/> of this
    /// device.</remarks>
    public void AddExternalWait(GpuExternalWait wait) {
        ArgumentOutOfRangeException.ThrowIfZero(wait.Value);

        if (wait.Fence is not (DirectXSharedFence or DirectXExportableFence)) {
            throw new ArgumentException(
                message: $"A Direct3D 12 submission waits only on a Direct3D 12 fence, not a {wait.Fence?.GetType().Name ?? "null"}.",
                paramName: nameof(wait)
            );
        }

        m_externalWaits.Add(item: wait);
    }
    /// <inheritdoc/>
    public void Submit(ReadOnlySpan<nint> commandBufferHandles) =>
        Execute(commandBufferHandles: commandBufferHandles);
    /// <inheritdoc/>
    public void Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {

        Execute(commandBufferHandles: commandBufferHandles);
        ((DirectXGpuSubmissionFence)fence).Arm(commandQueue: ((ID3D12CommandQueue*)deviceContext.CommandQueueHandle));
    }
    /// <inheritdoc/>
    public void SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) {
        Execute(commandBufferHandles: commandBufferHandles);
        deviceContext.WaitIdle();
    }
    /// <inheritdoc/>
    public IGpuSubmissionFence CreateSubmissionFence() =>
        new DirectXGpuSubmissionFence(device: ((ID3D12Device*)deviceContext.Device.Handle));

    private void Execute(ReadOnlySpan<nint> commandBufferHandles) {
        var queue = ((ID3D12CommandQueue*)deviceContext.CommandQueueHandle);

        if (commandBufferHandles.IsEmpty) {
            return;
        }

        if (m_externalWaits.Count != 0) {
            var calls = DirectXDeviceCommandCalls.Of(deviceContext: deviceContext);

            foreach (var wait in m_externalWaits) {
                DirectXCommandCalls.QueueWait(
                    calls: calls,
                    fence: ((ID3D12Fence*)((wait.Fence is DirectXSharedFence opened)
                        ? opened.FenceHandle
                        : ((DirectXExportableFence)wait.Fence).FenceHandle)),
                    queue: queue,
                    value: wait.Value
                );
            }

            m_externalWaits.Clear();
        }

        var lists = stackalloc ID3D12CommandList*[commandBufferHandles.Length];

        for (var i = 0; (i < commandBufferHandles.Length); i++) {
            var state = ((DirectXCommandBufferState)GCHandle.FromIntPtr(value: commandBufferHandles[i]).Target!);

            lists[i] = ((ID3D12CommandList*)state.CommandList);
        }

        queue->ExecuteCommandLists(
            NumCommandLists: ((uint)commandBufferHandles.Length),
            ppCommandLists: lists
        );
    }
}

/// <summary>
/// The Direct3D 12 <see cref="IGpuSubmissionFence"/>: a monotonic <c>ID3D12Fence</c> + auto-reset event (the same
/// pair <c>DirectXDeviceContext.WaitIdle</c> uses), signaled on the queue right after the fenced execute.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
file sealed unsafe class DirectXGpuSubmissionFence : IGpuSubmissionFence {
    // The device the fence is on; the context owns it, and a removal is read from it.
    private readonly nint m_device;

    private nint m_fence;
    private HANDLE m_fenceEvent;
    private ulong m_nextValue = 1UL;
    private ulong m_pendingValue; // 0 = no submission outstanding

    internal DirectXGpuSubmissionFence(ID3D12Device* device) {
        m_device = ((nint)device);
        device->CreateFence(
            Flags: default,
            InitialValue: 0,
            ppFence: out var fence,
            riid: ID3D12Fence.IID_Guid
        );
        m_fence = ((nint)fence);
        m_fenceEvent = PInvoke.CreateEvent(
            bInitialState: false,
            bManualReset: false,
            lpEventAttributes: ((SECURITY_ATTRIBUTES*)null),
            lpName: default(PCWSTR)
        );

        if (m_fenceEvent.IsNull) {
            _ = ((IUnknown*)m_fence)->Release();
            m_fence = 0;

            throw new DirectXException(
                operation: "CreateEventW",
                result: Marshal.GetHRForLastWin32Error()
            );
        }
    }

    /// <inheritdoc/>
    /// <remarks>Reads <c>ID3D12Fence::GetCompletedValue</c>. A removed device reports <c>UINT64_MAX</c>, a value this
    /// fence never signals, which surfaces as <see cref="DeviceLostException"/>.</remarks>
    public bool IsSignaled {
        get {
            if (0UL == m_pendingValue) {
                return true;
            }

            var completed = ((ID3D12Fence*)m_fence)->GetCompletedValue();

            if (completed == ulong.MaxValue) {
                throw new DeviceLostException(message: "ID3D12Fence::GetCompletedValue reported a removed device.");
            }

            return (completed >= m_pendingValue);
        }
    }

    /// <summary>Queues a signal for the just-executed submission; the caller must have drained any prior one first.</summary>
    internal void Arm(ID3D12CommandQueue* commandQueue) {
        if (0UL != m_pendingValue) {
            throw new InvalidOperationException(message: "A submission is already outstanding on this fence; Wait before re-arming it.");
        }

        m_pendingValue = m_nextValue++;
        DirectXCommandCalls.Signal(
            calls: new DirectXDeviceCommandCalls(device: ((ID3D12Device*)m_device)),
            fence: ((ID3D12Fence*)m_fence),
            queue: commandQueue,
            value: m_pendingValue
        );
    }

    /// <inheritdoc/>
    public void Wait() {
        if (0UL == m_pendingValue) {
            return;
        }

        DirectXCommandCalls.Wait(
            calls: new DirectXDeviceCommandCalls(device: ((ID3D12Device*)m_device)),
            fence: ((ID3D12Fence*)m_fence),
            fenceEvent: m_fenceEvent,
            value: m_pendingValue
        );
        m_pendingValue = 0UL;
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (0 != m_fence) {
            _ = ((IUnknown*)m_fence)->Release();
            m_fence = 0;
        }

        if (!m_fenceEvent.IsNull) {
            _ = PInvoke.CloseHandle(hObject: m_fenceEvent);
            m_fenceEvent = HANDLE.Null;
        }

        m_pendingValue = 0UL;
    }
}
