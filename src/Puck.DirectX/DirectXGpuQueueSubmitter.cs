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
/// later one on the queue wait for the shared fence on the GPU. While a batch is open a submission joins the shared
/// <see cref="GpuSubmissionBatch{TEntry}"/>, whose lists reach the queue in one <c>ExecuteCommandLists</c>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuQueueSubmitter : IGpuQueueSubmitter {
    private readonly DirectXDeviceContext m_deviceContext;
    private readonly List<GpuExternalWait> m_externalWaits = [];
    private readonly Batch m_batch;
    // The drain a fence whose signal was refused runs behind the lists already executed (DirectXGpuSubmissionFence.Arm).
    private readonly Action m_drainQueue;

    /// <summary>Initializes a new instance of the <see cref="DirectXGpuQueueSubmitter"/> class, submitting to the
    /// context's direct command queue.</summary>
    /// <param name="deviceContext">The device context whose queue receives the submissions; it owns the submitter.</param>
    public DirectXGpuQueueSubmitter(DirectXDeviceContext deviceContext) {
        m_deviceContext = deviceContext;
        m_batch = new Batch(submitter: this);
        m_drainQueue = deviceContext.WaitIdle;
    }

    /// <inheritdoc/>
    /// <remarks>Takes a <see cref="DirectXSharedFence"/> or a <see cref="DirectXExportableFence"/> of this
    /// device. A wait still outstanding splits an open batch, since <c>ID3D12CommandQueue::Wait</c> holds everything after
    /// it on the queue.</remarks>
    public void AddExternalWait(GpuExternalWait wait) {
        ArgumentOutOfRangeException.ThrowIfZero(wait.Value);

        if (wait.Fence is not (DirectXSharedFence or DirectXExportableFence)) {
            throw new ArgumentException(
                message: $"A Direct3D 12 submission waits only on a Direct3D 12 fence, not a {(wait.Fence?.GetType().Name ?? "null")}.",
                paramName: nameof(wait)
            );
        }

        if (GpuQueueWork.Process.Waits(reached: wait.IsReached)) { return; }
        // A wait belongs after earlier submissions, never before a producer that may signal it.
        m_batch.Split(cause: GpuQueueCause.ExternalWait);

        m_externalWaits.Add(item: wait);
    }
    /// <inheritdoc/>
    public void Submit(ReadOnlySpan<nint> commandBufferHandles) {
        if (m_batch.TryDefer(entries: commandBufferHandles, fence: null, lists: commandBufferHandles.Length)) { return; }
        Execute(cause: GpuQueueCause.Unbatched, commandBufferHandles: commandBufferHandles);
    }
    /// <inheritdoc/>
    public void Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        var native = ((DirectXGpuSubmissionFence)fence);

        if (m_batch.TryDefer(entries: commandBufferHandles, fence: native, lists: commandBufferHandles.Length)) { return; }
        // An empty fenced submission still completes all work submitted before it.
        if (commandBufferHandles.IsEmpty) { m_batch.Split(cause: GpuQueueCause.EmptyFence); }
        native.RequireDrained();
        try {
            Execute(cause: GpuQueueCause.Unbatched, commandBufferHandles: commandBufferHandles);
        } catch {
            // A refused external wait comes before ExecuteCommandLists, the one call that cannot fail, so nothing of this
            // submission reached the queue.
            native.Abandon();

            throw;
        }
        native.Arm(commandQueue: ((ID3D12CommandQueue*)m_deviceContext.SubmissionQueueHandle), drainQueue: m_drainQueue);
    }
    /// <inheritdoc/>
    public void SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) {
        m_batch.Split(cause: GpuQueueCause.Drain);
        Execute(cause: GpuQueueCause.Drain, commandBufferHandles: commandBufferHandles);
        m_deviceContext.WaitIdle();
    }
    /// <inheritdoc/>
    public void BeginBatch() => m_batch.Begin();
    /// <inheritdoc/>
    public void FlushBatch() => m_batch.Flush();
    /// <summary>Hands the deferred submissions to the queue and keeps the batch open
    /// (<see cref="GpuSubmissionBatch.Split"/>). Every read of the context's queue handle
    /// (<see cref="DirectXDeviceContext.CommandQueueHandle"/>) calls it.</summary>
    internal void Split() => m_batch.Split(cause: GpuQueueCause.QueueDoor);
    /// <inheritdoc/>
    public IGpuSubmissionFence CreateSubmissionFence() =>
        new DirectXGpuSubmissionFence(device: ((ID3D12Device*)m_deviceContext.Device.Handle));

    // Counts the execute it makes; a batch's hand-off is counted by the batch and passes no cause.
    private void Execute(ReadOnlySpan<nint> commandBufferHandles, GpuQueueCause? cause) {
        var queue = ((ID3D12CommandQueue*)m_deviceContext.SubmissionQueueHandle);

        if (commandBufferHandles.IsEmpty) {
            return;
        }
        if (cause is { } counted) { GpuQueueWork.Process.Execute(cause: counted, lists: commandBufferHandles.Length); }

        if (m_externalWaits.Count != 0) {
            var calls = DirectXDeviceCommandCalls.Of(deviceContext: m_deviceContext);

            // The list is spent even when a wait fails, so a device loss never carries a lost device's fence onward.
            try {
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
            } finally {
                m_externalWaits.Clear();
            }
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

    // The deferred command lists reach the queue in one ExecuteCommandLists, behind any external waits, with the owner's
    // signal queued right after them.
    private sealed class Batch(DirectXGpuQueueSubmitter submitter) : GpuSubmissionBatch<nint> {
        protected override void Submit(ReadOnlySpan<nint> entries, GpuBatchedSubmissionFence? owner) {
            submitter.Execute(cause: null, commandBufferHandles: entries);
            ((DirectXGpuSubmissionFence?)owner)?.Arm(commandQueue: ((ID3D12CommandQueue*)submitter.m_deviceContext.SubmissionQueueHandle), drainQueue: submitter.m_drainQueue);
        }
    }
}

/// <summary>
/// The Direct3D 12 <see cref="IGpuSubmissionFence"/>: a monotonic <c>ID3D12Fence</c> + auto-reset event (the same
/// pair <c>DirectXDeviceContext.WaitIdle</c> uses), signaled on the queue right after the fenced execute. A drained fence
/// is replaced before its next value could equal the device-loss sentinel <c>UINT64_MAX</c>. Its batch and alias states
/// are the shared <see cref="GpuBatchedSubmissionFence"/>'s.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuSubmissionFence : GpuBatchedSubmissionFence {
    // The device the fence is on; the context owns it, and a removal is read from it.
    private readonly nint m_device;

    private nint m_fence;
    private HANDLE m_fenceEvent;
    private ulong m_nextValue = 1UL;
    private ulong m_pendingValue; // 0 = no submission outstanding

    /// <summary>Creates a reusable submission fence on the caller-owned device.</summary>
    /// <param name="device">The device, retained by the caller until the fence is disposed.</param>
    public DirectXGpuSubmissionFence(ID3D12Device* device) {
        m_device = ((nint)device);
        Renew();
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

    /// <summary>Queues a signal for the just-executed submission, and arms the fence only once the signal is on the queue;
    /// the caller must have drained any prior one first, or the submission is the one its batch deferred. The lists are
    /// already executed, so a refused signal cannot leave the fence reading drained: the queue is drained behind them and
    /// the fence marked run, or, when that drain fails too, marked faulted with the drain's failure. The signal's failure
    /// is rethrown either way.</summary>
    /// <param name="commandQueue">The queue the submission was executed on.</param>
    /// <param name="drainQueue">Blocks until the queue has finished everything executed on it (the device context's
    /// <see cref="IGpuDeviceContext.WaitIdle"/>).</param>
    /// <exception cref="InvalidOperationException">A submission is outstanding.</exception>
    public void Arm(ID3D12CommandQueue* commandQueue, Action drainQueue) =>
        ArmBehind(
            drainQueue: drainQueue,
            queueSignal: static signal => signal.Fence.Signal(commandQueue: ((ID3D12CommandQueue*)signal.Queue)),
            state: (Fence: this, Queue: ((nint)commandQueue))
        );

    // Queues the next value's signal; the value becomes the pending one only once the call returned.
    private void Signal(ID3D12CommandQueue* commandQueue) {
        // Also retries a renewal that failed after the preceding Wait drained the old native fence.
        if (m_nextValue == ulong.MaxValue) { Renew(); }
        DirectXCommandCalls.Signal(
            calls: new DirectXDeviceCommandCalls(device: ((ID3D12Device*)m_device)),
            fence: ((ID3D12Fence*)m_fence),
            queue: commandQueue,
            value: m_nextValue
        );
        m_pendingValue = m_nextValue++;
    }

    /// <summary>Marks the submission abandoned once its execute failed before reaching the queue
    /// (<see cref="GpuBatchedSubmissionFence.MarkAbandoned"/>).</summary>
    /// <exception cref="InvalidOperationException">A submission is outstanding.</exception>
    internal void Abandon() => MarkAbandoned();

    /// <inheritdoc/>
    /// <remarks>Reads <c>ID3D12Fence::GetCompletedValue</c>. A removed device reports <c>UINT64_MAX</c>, a value this
    /// fence never signals, which surfaces as <see cref="DeviceLostException"/>.</remarks>
    protected override bool NativeSignaled() {
        var completed = ((ID3D12Fence*)m_fence)->GetCompletedValue();

        if (completed == ulong.MaxValue) {
            var reason = new DirectXDeviceCommandCalls(device: ((ID3D12Device*)m_device)).DeviceRemovedReason();

            throw new DeviceLostException(
                message: $"ID3D12Fence::GetCompletedValue reported a removed device (reason 0x{reason.Value:X8}).",
                reasonCode: reason.Value
            );
        }

        return (completed >= m_pendingValue);
    }
    /// <inheritdoc/>
    protected override void WaitNative() => DirectXCommandCalls.Wait(
        calls: new DirectXDeviceCommandCalls(device: ((ID3D12Device*)m_device)),
        fence: ((ID3D12Fence*)m_fence),
        fenceEvent: m_fenceEvent,
        value: m_pendingValue
    );
    /// <inheritdoc/>
    /// <remarks>Asks <c>ID3D12Fence::SetEventOnCompletion</c> with the event's own handle, which the fence sets when it
    /// reaches the pending value, at once when it already has, and when the device is removed. A refused call sets the
    /// event here, so the waiter polls.</remarks>
    protected override void SignalNative(EventWaitHandle signal) {
        var armed = new DirectXDeviceCommandCalls(device: ((ID3D12Device*)m_device)).SetEventOnCompletion(
            fence: ((ID3D12Fence*)m_fence),
            fenceEvent: ((HANDLE)signal.SafeWaitHandle.DangerousGetHandle()),
            value: m_pendingValue
        );

        if (armed.Failed) { _ = signal.Set(); }
    }
    /// <inheritdoc/>
    protected override void DrainNative() {
        m_pendingValue = 0UL;
        if (m_nextValue == ulong.MaxValue) { Renew(); }
    }
    /// <inheritdoc/>
    protected override void DisposeNative() {
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

    // Only called with no submission outstanding. Create before releasing the old fence, so failed allocation leaves
    // a valid, drained object; the old completion value can never satisfy a submission in the new sequence.
    private void Renew() {
        ((ID3D12Device*)m_device)->CreateFence(Flags: default, InitialValue: 0, ppFence: out ID3D12Fence* fence);
        if (m_fence != 0) { _ = ((IUnknown*)m_fence)->Release(); }
        m_fence = (nint)fence;
        m_nextValue = 1;
    }
}
