using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
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
/// <c>ID3D12Fence</c> + event pair signaled on the queue right after the fenced execute.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuQueueSubmitter : IGpuQueueSubmitter {
    /// <inheritdoc/>
    public void Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) =>
        Execute(deviceContext: deviceContext, commandBufferHandles: commandBufferHandles);

    /// <inheritdoc/>
    public void Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        var dxContext = (IDirectXDeviceContext)deviceContext;

        Execute(deviceContext: deviceContext, commandBufferHandles: commandBufferHandles);
        ((DirectXGpuSubmissionFence)fence).Arm(commandQueue: (ID3D12CommandQueue*)dxContext.CommandQueueHandle);
    }

    /// <inheritdoc/>
    public void SubmitAndWait(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) {
        Execute(deviceContext: deviceContext, commandBufferHandles: commandBufferHandles);
        deviceContext.WaitIdle();
    }

    /// <inheritdoc/>
    public IGpuSubmissionFence CreateSubmissionFence(IGpuDeviceContext deviceContext) =>
        new DirectXGpuSubmissionFence(device: (ID3D12Device*)deviceContext.DeviceHandle);

    private static void Execute(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) {
        var dxContext = (IDirectXDeviceContext)deviceContext;
        var queue = (ID3D12CommandQueue*)dxContext.CommandQueueHandle;
        var lists = stackalloc ID3D12CommandList*[commandBufferHandles.Length];

        for (var i = 0; (i < commandBufferHandles.Length); i++) {
            var state = (DirectXCommandBufferState)GCHandle.FromIntPtr(value: commandBufferHandles[i]).Target!;

            lists[i] = (ID3D12CommandList*)state.CommandList;
        }

        queue->ExecuteCommandLists((uint)commandBufferHandles.Length, lists);
    }
}

/// <summary>
/// The Direct3D 12 <see cref="IGpuSubmissionFence"/>: a monotonic <c>ID3D12Fence</c> + auto-reset event (the same
/// pair <c>DirectXDeviceContext.WaitIdle</c> uses), signaled on the queue right after the fenced execute.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
file sealed unsafe class DirectXGpuSubmissionFence : IGpuSubmissionFence {
    private nint m_fence;
    private HANDLE m_fenceEvent;
    private ulong m_nextValue = 1UL;
    private ulong m_pendingValue; // 0 = no submission outstanding

    internal DirectXGpuSubmissionFence(ID3D12Device* device) {
        device->CreateFence(
            InitialValue: 0,
            Flags: default,
            riid: ID3D12Fence.IID_Guid,
            ppFence: out var fence
        );
        m_fence = (nint)fence;
        m_fenceEvent = PInvoke.CreateEvent(
            lpEventAttributes: (SECURITY_ATTRIBUTES*)null,
            bManualReset: false,
            bInitialState: false,
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

    /// <summary>Queues a signal for the just-executed submission; the caller must have drained any prior one first.</summary>
    internal void Arm(ID3D12CommandQueue* commandQueue) {
        if (0UL != m_pendingValue) {
            throw new InvalidOperationException(message: "A submission is already outstanding on this fence; Wait before re-arming it.");
        }

        m_pendingValue = m_nextValue++;
        commandQueue->Signal((ID3D12Fence*)m_fence, m_pendingValue);
    }

    /// <inheritdoc/>
    public void Wait() {
        if (0UL == m_pendingValue) {
            return;
        }

        var fence = (ID3D12Fence*)m_fence;

        if (fence->GetCompletedValue() < m_pendingValue) {
            fence->SetEventOnCompletion(m_pendingValue, m_fenceEvent);
            _ = PInvoke.WaitForSingleObject(hHandle: m_fenceEvent, dwMilliseconds: uint.MaxValue);
        }

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
