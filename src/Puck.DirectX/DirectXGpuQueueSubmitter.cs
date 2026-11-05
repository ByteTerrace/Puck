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
    private readonly List<nint> m_lists = [];
    private readonly List<DirectXGpuSubmissionFence> m_fences = [];
    private bool m_batch;

    /// <inheritdoc/>
    /// <remarks>Takes a <see cref="DirectXSharedFence"/> or a <see cref="DirectXExportableFence"/> of this
    /// device.</remarks>
    public void AddExternalWait(GpuExternalWait wait) {
        ArgumentOutOfRangeException.ThrowIfZero(wait.Value);

        if (wait.Fence is not (DirectXSharedFence or DirectXExportableFence)) {
            throw new ArgumentException(
                message: $"A Direct3D 12 submission waits only on a Direct3D 12 fence, not a {(wait.Fence?.GetType().Name ?? "null")}.",
                paramName: nameof(wait)
            );
        }

        // A wait belongs after earlier submissions, never before a producer that may signal it.
        Split();

        m_externalWaits.Add(item: wait);
    }
    /// <inheritdoc/>
    public void Submit(ReadOnlySpan<nint> commandBufferHandles) {
        if (Defer(commandBufferHandles, null)) { return; }
        Execute(commandBufferHandles);
    }
    /// <inheritdoc/>
    public void Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {

        var native = (DirectXGpuSubmissionFence)fence;
        if (Defer(commandBufferHandles, native)) { return; }
        // An empty fenced submission still completes all work submitted before it.
        if (commandBufferHandles.IsEmpty) { Split(); }
        native.RequireDrained();
        Execute(commandBufferHandles: commandBufferHandles);
        native.Arm(commandQueue: ((ID3D12CommandQueue*)deviceContext.SubmissionQueueHandle));
    }
    /// <inheritdoc/>
    public void SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) {
        Split();
        Execute(commandBufferHandles: commandBufferHandles);
        deviceContext.WaitIdle();
    }
    /// <inheritdoc/>
    public void BeginBatch() => m_batch = true;
    /// <inheritdoc/>
    public void FlushBatch() {
        m_batch = false;
        if (m_lists.Count == 0) { return; }
        var owner = m_fences.Count == 0 ? null : m_fences[^1];
        try {
            Execute(CollectionsMarshal.AsSpan(m_lists));
            owner?.Arm((ID3D12CommandQueue*)deviceContext.SubmissionQueueHandle);
        } finally {
            // Even after a failed execute or signal, every fence answers from the owner, whose removed device then
            // surfaces as a loss instead of leaving the others deferred in a batch that no longer exists.
            foreach (var fence in m_fences) {
                if (!ReferenceEquals(fence, owner)) { fence.Alias(owner!); }
            }
            m_lists.Clear();
            m_fences.Clear();
        }
    }
    /// <summary>Hands the deferred submissions to the queue and keeps the batch open: whatever reaches the queue next
    /// follows every submission made before it, and later submissions are still deferred. Every read of the context's
    /// queue handle (<see cref="DirectXDeviceContext.CommandQueueHandle"/>) calls it.</summary>
    internal void Split() {
        var open = m_batch;
        FlushBatch();
        m_batch = open;
    }

    private bool Defer(ReadOnlySpan<nint> handles, DirectXGpuSubmissionFence? fence) {
        if (!m_batch || handles.IsEmpty) { return false; }
        fence?.Defer(this);
        foreach (var handle in handles) { m_lists.Add(handle); }
        if (fence is not null) { m_fences.Add(fence); }
        return true;
    }
    /// <inheritdoc/>
    public IGpuSubmissionFence CreateSubmissionFence() =>
        new DirectXGpuSubmissionFence(device: ((ID3D12Device*)deviceContext.Device.Handle));

    private void Execute(ReadOnlySpan<nint> commandBufferHandles) {
        var queue = ((ID3D12CommandQueue*)deviceContext.SubmissionQueueHandle);

        if (commandBufferHandles.IsEmpty) {
            return;
        }

        if (m_externalWaits.Count != 0) {
            var calls = DirectXDeviceCommandCalls.Of(deviceContext: deviceContext);

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
        DirectXSubmissionWork.Process.Execute(commandBufferHandles.Length);
    }
}

/// <summary>
/// The Direct3D 12 <see cref="IGpuSubmissionFence"/>: a monotonic <c>ID3D12Fence</c> + auto-reset event (the same
/// pair <c>DirectXDeviceContext.WaitIdle</c> uses), signaled on the queue right after the fenced execute. A drained fence
/// is replaced before its next value could equal the device-loss sentinel <c>UINT64_MAX</c>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuSubmissionFence : IGpuSubmissionFence {
    // The device the fence is on; the context owns it, and a removal is read from it.
    private readonly nint m_device;

    private nint m_fence;
    private HANDLE m_fenceEvent;
    private ulong m_nextValue = 1UL;
    private ulong m_pendingValue; // 0 = no submission outstanding
    private DirectXGpuQueueSubmitter? m_deferred;
    private DirectXGpuSubmissionFence? m_alias;
    private long m_aliasGeneration;
    private long m_generation;

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

    /// <inheritdoc/>
    /// <remarks>Reads <c>ID3D12Fence::GetCompletedValue</c>. A removed device reports <c>UINT64_MAX</c>, a value this
    /// fence never signals, which surfaces as <see cref="DeviceLostException"/>.</remarks>
    public bool IsSignaled {
        get {
            if (m_deferred is not null) { return false; }
            if (m_alias is { } alias) { return alias.m_generation != m_aliasGeneration || alias.IsSignaled; }
            if (0UL == m_pendingValue) {
                return true;
            }

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
    }

    /// <summary>Queues a signal for the just-executed submission; the caller must have drained any prior one first.</summary>
    /// <param name="commandQueue">The queue the submission was executed on.</param>
    public void Arm(ID3D12CommandQueue* commandQueue) {
        if (m_deferred is null) { RequireDrained(); }
        m_deferred = null;
        // Also retries a renewal that failed after the preceding Wait drained the old native fence.
        if (m_nextValue == ulong.MaxValue) { Renew(); }

        m_pendingValue = m_nextValue++;
        DirectXCommandCalls.Signal(
            calls: new DirectXDeviceCommandCalls(device: ((ID3D12Device*)m_device)),
            fence: ((ID3D12Fence*)m_fence),
            queue: commandQueue,
            value: m_pendingValue
        );
        DirectXSubmissionWork.Process.Signal();
    }

    internal void RequireDrained() {
        if (m_pendingValue != 0 || m_deferred is not null || m_alias is not null) {
            throw new InvalidOperationException("A submission is already outstanding on this fence; Wait before re-arming it.");
        }
    }
    internal void Defer(DirectXGpuQueueSubmitter batch) { RequireDrained(); m_deferred = batch; }
    internal void Alias(DirectXGpuSubmissionFence owner) {
        m_deferred = null;
        m_alias = owner;
        m_aliasGeneration = owner.m_generation;
    }

    /// <inheritdoc/>
    public void Wait() {
        m_deferred?.FlushBatch();
        if (m_alias is { } alias) {
            if (alias.m_generation == m_aliasGeneration) {
                DirectXCommandCalls.Wait(calls: new DirectXDeviceCommandCalls((ID3D12Device*)m_device),
                    fence: (ID3D12Fence*)alias.m_fence, fenceEvent: alias.m_fenceEvent, value: alias.m_pendingValue);
            }
            m_alias = null;
            m_generation++;
            return;
        }
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
        m_generation++;
        if (m_nextValue == ulong.MaxValue) { Renew(); }
    }

    // Only called with no submission outstanding. Create before releasing the old fence, so failed allocation leaves
    // a valid, drained object; the old completion value can never satisfy a submission in the new sequence.
    private void Renew() {
        ((ID3D12Device*)m_device)->CreateFence(Flags: default, InitialValue: 0, ppFence: out ID3D12Fence* fence);
        if (m_fence != 0) { _ = ((IUnknown*)m_fence)->Release(); }
        m_fence = (nint)fence;
        m_nextValue = 1;
    }
    /// <inheritdoc/>
    public void Dispose() {
        m_deferred = null;
        m_alias = null;
        m_generation++;
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
