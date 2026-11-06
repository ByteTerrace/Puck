using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.System.Com;

namespace Puck.Platform.Windows;

/// <summary>
/// Orders a Direct3D 11 producer's writes into shared targets before a consumer on another device reads them. With the
/// consumer's shared fence (a Direct3D 12 fence created with <c>D3D12_FENCE_FLAG_SHARED</c>) opened through
/// <c>ID3D11Device5::OpenSharedFence</c>, <see cref="Complete()"/> signals the next value on the immediate context and
/// returns it, and the consumer's submission waits for that value on the GPU. A device that cannot open the fence, or a
/// producer offered none, keeps the CPU wait: <see cref="Complete()"/> signals a fence of the context's own, arms an
/// operating-system event on it (<c>ID3D11Fence::SetEventOnCompletion</c>) and blocks on that event, so the thread sleeps
/// until the write has finished and returns zero. The wait is bounded by <see cref="CompletionTimeout"/>, after which it
/// throws a <see cref="COMException"/> naming the device's removal reason or <c>DXGI_ERROR_DEVICE_HUNG</c> rather than
/// wait forever. <see cref="Order"/> says which way the writes are ordered. Affine to the producer's thread; the caller
/// holds the device's critical section when the context is shared.
/// </summary>
[SupportedOSPlatform("windows8.0")]
public sealed unsafe class Win32D3D11CompletionSignal : IDisposable {
    internal const int Windows10CreatorsUpdateBuild = 15063;

    private const int DeviceHungHResult = unchecked((int)0x887A0006);

    /// <summary>The longest the CPU wait blocks before it reports a removed or hung device: Windows' own timeout
    /// detection resets a device whose work has not retired within two seconds, so a wait that outlasts five times that
    /// has no GPU work behind it.</summary>
    public static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    private readonly ID3D11Device* m_device;
    private readonly ID3D11DeviceContext* m_context;

    private ManualResetEvent? m_completed;
    private ID3D11DeviceContext4* m_context4;
    private ID3D11Fence* m_fence;
    private ulong m_lastValue;

    /// <summary>Initializes a new instance of the <see cref="Win32D3D11CompletionSignal"/> class on a device, opening
    /// the shared fence when one is offered and the device can open it, and otherwise creating the context's own fence
    /// for the CPU wait.</summary>
    /// <param name="device">The producer's device; stays owned by the caller and outlives this signal.</param>
    /// <param name="context">The device's immediate context the writes are recorded on; stays owned by the caller.</param>
    /// <param name="sharedFenceHandle">The consumer's shared fence NT handle, or zero to keep the CPU wait; stays owned
    /// by the caller.</param>
    /// <exception cref="PlatformNotSupportedException">The device can create no fence, which the CPU wait needs (Windows
    /// 10 version 1703 added Direct3D 11 fences).</exception>
    public Win32D3D11CompletionSignal(nint device, nint context, nint sharedFenceHandle) {
        m_device = ((ID3D11Device*)device);
        m_context = ((ID3D11DeviceContext*)context);
        Order = ((0 == sharedFenceHandle)
            ? new SharedFenceOrder(
                Reason: "no shared fence was offered",
                SharedFence: false
            )
            : TryOpenFence(
                context: m_context,
                context4: out m_context4,
                createOwn: false,
                device: m_device,
                fence: out m_fence,
                sharedFenceHandle: sharedFenceHandle
            ));

        if (Order.SharedFence) {
            return;
        }

        var own = TryOpenFence(
            context: m_context,
            context4: out m_context4,
            createOwn: true,
            device: m_device,
            fence: out m_fence,
            sharedFenceHandle: 0
        );

        if (!own.SharedFence) {
            throw new PlatformNotSupportedException(message: $"the CPU wait needs a Direct3D 11 fence of the context's own: {own.Reason}");
        }

        m_completed = new ManualResetEvent(initialState: false);
    }

    /// <summary>Gets how this signal orders the producer's writes: through the shared fence, or by the CPU wait and
    /// why.</summary>
    public SharedFenceOrder Order { get; }

    /// <summary>Completes the writes recorded on the immediate context so far for another device to read: signals the
    /// shared fence's next value and flushes, or blocks the thread until they have finished.</summary>
    /// <returns>The fence value the writes signal, which the consumer's submission waits for; zero when they finished
    /// before this call returned.</returns>
    /// <exception cref="ObjectDisposedException">The signal was disposed.</exception>
    /// <exception cref="COMException">The device was removed, or its work did not finish within
    /// <see cref="CompletionTimeout"/>.</exception>
    public ulong Complete() => Complete(value: (m_lastValue + 1UL));
    /// <summary>Completes the writes recorded on the immediate context so far for another device to read, signalling a
    /// value another counter hands out: the ring the writes land in, when more than one producer signals its fence over
    /// time (<see cref="LatestSlotPublication.NextFenceValue"/>).</summary>
    /// <param name="value">The value the shared fence is set to; greater than every value the fence was set to before.
    /// The CPU wait counts values of its own and ignores it.</param>
    /// <returns><paramref name="value"/>, which the consumer's submission waits for; zero when the writes finished before
    /// this call returned.</returns>
    /// <exception cref="ObjectDisposedException">The signal was disposed.</exception>
    /// <exception cref="COMException">The device was removed, or its work did not finish within
    /// <see cref="CompletionTimeout"/>.</exception>
    public ulong Complete(ulong value) {
        ObjectDisposedException.ThrowIf(
            condition: (m_fence is null),
            instance: this
        );

        using var attribution = GpuQueueWork.Attribute(category: GpuQueueCategory.Transfer);

        if (Order.SharedFence) {
            m_lastValue = value;
            GpuQueueWork.Process.Signal();
            m_context4->Signal(
                Value: value,
                pFence: m_fence
            );
            m_context->Flush();

            return value;
        }

        WaitForCompletion();

        return 0UL;
    }
    /// <inheritdoc/>
    public void Dispose() {
        Release(value: m_fence);
        m_fence = null;
        Release(value: m_context4);
        m_context4 = null;
        m_completed?.Dispose();
        m_completed = null;
    }

    // Signals the context's own fence behind everything recorded so far and sleeps on an event the fence sets once that
    // value is reached. The event is armed after the signal is queued: a value already reached sets it at once. On a
    // timeout the device's removal reason names the fault, and a device still alive is reported hung. Issued on a
    // producer's thread at its cadence, never on the render thread.
    private void WaitForCompletion() {
        var value = ++m_lastValue;
        var completed = m_completed!;

        _ = completed.Reset();
        GpuQueueWork.Process.Signal();
        m_context4->Signal(
            Value: value,
            pFence: m_fence
        );
        m_context->Flush();
        m_fence->SetEventOnCompletion(
            Value: value,
            hEvent: new HANDLE(value: ((void*)completed.SafeWaitHandle.DangerousGetHandle()))
        );

        if (completed.WaitOne(timeout: CompletionTimeout)) {
            return;
        }

        // The generated wrapper throws the removal reason as a failed HRESULT and returns when the device is alive.
        var reason = 0;

        try {
            m_device->GetDeviceRemovedReason();
        } catch (COMException removal) {
            reason = removal.HResult;
        }

        throw new COMException(
            errorCode: ((0 != reason) ? reason : DeviceHungHResult),
            message: ((0 != reason)
                ? $"the Direct3D 11 device was removed (0x{reason:X8}) before its writes finished"
                : $"the Direct3D 11 device's writes did not finish within {CompletionTimeout.TotalSeconds:0} seconds"
            )
        );
    }

    // Opens a Direct3D 12 shared fence on a Direct3D 11 device, or creates a fence of the device's own, with the
    // immediate context's ID3D11DeviceContext4 that signals and waits on it; both pointers are owned by the caller on
    // success and null on refusal.
    internal static SharedFenceOrder TryOpenFence(ID3D11Device* device, ID3D11DeviceContext* context, nint sharedFenceHandle, bool createOwn, out ID3D11DeviceContext4* context4, out ID3D11Fence* fence) {
        context4 = null;
        fence = null;

        if (!OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: Windows10CreatorsUpdateBuild
        )) {
            return new SharedFenceOrder(
                Reason: "Direct3D 11 fences need Windows 10 version 1703",
                SharedFence: false
            );
        }

        if (((IUnknown*)device)->QueryInterface<ID3D11Device5>(ppvObject: out var device5).Failed) {
            return new SharedFenceOrder(
                Reason: "the Direct3D 11 device has no ID3D11Device5",
                SharedFence: false
            );
        }

        try {
            if (((IUnknown*)context)->QueryInterface<ID3D11DeviceContext4>(ppvObject: out var queried).Failed) {
                return new SharedFenceOrder(
                    Reason: "the Direct3D 11 immediate context has no ID3D11DeviceContext4",
                    SharedFence: false
                );
            }

            void* opened = null;
            var fenceIid = ID3D11Fence.IID_Guid;

            try {
                if (createOwn) {
                    device5->CreateFence(
                        InitialValue: 0UL,
                        Flags: D3D11_FENCE_FLAG.D3D11_FENCE_FLAG_NONE,
                        ReturnedInterface: &fenceIid,
                        ppFence: &opened
                    );
                } else {
                    device5->OpenSharedFence(
                        hFence: new HANDLE(value: ((void*)sharedFenceHandle)),
                        ReturnedInterface: &fenceIid,
                        ppFence: &opened
                    );
                }
            } catch (Exception exception) when ((exception is COMException or ArgumentException or UnauthorizedAccessException)) {
                // The HRESULT mapping throws a handle that is no fence (E_INVALIDARG) as an ArgumentException and one
                // this process may not open (E_ACCESSDENIED) as an UnauthorizedAccessException; each is the device
                // refusing the fence, not a fault of the caller.
                _ = ((IUnknown*)queried)->Release();

                return new SharedFenceOrder(
                    Reason: $"ID3D11Device5::{(createOwn ? "CreateFence" : "OpenSharedFence")} refused the fence (0x{exception.HResult:X8})",
                    SharedFence: false
                );
            }

            context4 = queried;
            fence = ((ID3D11Fence*)opened);

            return new SharedFenceOrder(
                Reason: "",
                SharedFence: true
            );
        } finally {
            _ = ((IUnknown*)device5)->Release();
        }
    }
    internal static void Release<T>(T* value) where T : unmanaged {
        if (value is not null) {
            _ = ((IUnknown*)value)->Release();
        }
    }
}
/// <summary>
/// Orders a Direct3D 11 consumer's reads of shared targets after a producer on another device wrote them: the other
/// direction of <see cref="Win32D3D11CompletionSignal"/>. The producer's shared fence (a Direct3D 12 fence created with
/// <c>D3D12_FENCE_FLAG_SHARED</c>, which the producer signals after each write) is opened through
/// <c>ID3D11Device5::OpenSharedFence</c>, and <see cref="Wait"/> queues <c>ID3D11DeviceContext4::Wait</c> on the immediate
/// context, so work recorded after it starts only once the fence reaches the value and nothing blocks on the CPU. A
/// device that cannot open the fence has no wait (<see cref="Order"/> says why), and its reader refuses the producer's
/// targets rather than read a write in flight. Affine to the consumer's thread; the caller holds the device's critical
/// section when the context is shared.
/// </summary>
[SupportedOSPlatform("windows8.0")]
public sealed unsafe class Win32D3D11FenceWait : IDisposable {
    private ID3D11DeviceContext4* m_context4;
    private ID3D11Fence* m_fence;

    /// <summary>Initializes a new instance of the <see cref="Win32D3D11FenceWait"/> class on a device, opening the
    /// producer's shared fence.</summary>
    /// <param name="device">The consumer's device; stays owned by the caller and outlives this wait.</param>
    /// <param name="context">The device's immediate context the reads are recorded on; stays owned by the caller.</param>
    /// <param name="sharedFenceHandle">The producer's shared fence NT handle; stays owned by the caller.</param>
    public Win32D3D11FenceWait(nint device, nint context, nint sharedFenceHandle) {
        Order = Win32D3D11CompletionSignal.TryOpenFence(
            context: ((ID3D11DeviceContext*)context),
            context4: out m_context4,
            createOwn: false,
            device: ((ID3D11Device*)device),
            fence: out m_fence,
            sharedFenceHandle: sharedFenceHandle
        );
    }

    /// <summary>Gets whether the fence opened, and why not when it did not.</summary>
    public SharedFenceOrder Order { get; }

    /// <summary>Queues a wait on the immediate context: work recorded after the call starts only once the producer's
    /// fence reaches the value.</summary>
    /// <param name="value">The value the producer's write signals; zero, which a producer publishes for a write that
    /// finished before publication, queues nothing.</param>
    /// <exception cref="InvalidOperationException">The fence did not open (<see cref="Order"/>), or the wait was
    /// disposed.</exception>
    /// <exception cref="COMException">The device was removed.</exception>
    public void Wait(ulong value) {
        if (0UL == value) {
            return;
        }
        if (
            (m_fence is null) ||
            !OperatingSystem.IsWindowsVersionAtLeast(
                major: 10,
                minor: 0,
                build: Win32D3D11CompletionSignal.Windows10CreatorsUpdateBuild
            )
        ) {
            throw new InvalidOperationException(message: $"the producer's shared fence is not open: {Order}");
        }

        m_context4->Wait(
            Value: value,
            pFence: m_fence
        );
    }
    /// <inheritdoc/>
    public void Dispose() {
        Win32D3D11CompletionSignal.Release(value: m_fence);
        m_fence = null;
        Win32D3D11CompletionSignal.Release(value: m_context4);
        m_context4 = null;
    }
}
