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
/// producer offered none, keeps the CPU wait: <see cref="Complete()"/> spins on an event query until the write has
/// finished and returns zero. <see cref="Order"/> says which. Affine to the producer's thread; the caller holds the
/// device's critical section when the context is shared.
/// </summary>
[SupportedOSPlatform("windows8.0")]
public sealed unsafe class Win32D3D11CompletionSignal : IDisposable {
    internal const int Windows10CreatorsUpdateBuild = 15063;

    private readonly ID3D11DeviceContext* m_context;

    private ID3D11DeviceContext4* m_context4;
    private ID3D11Fence* m_fence;
    private ulong m_lastValue;
    private ID3D11Query* m_query;

    /// <summary>Initializes a new instance of the <see cref="Win32D3D11CompletionSignal"/> class on a device, opening
    /// the shared fence when one is offered and the device can open it.</summary>
    /// <param name="device">The producer's device; stays owned by the caller and outlives this signal.</param>
    /// <param name="context">The device's immediate context the writes are recorded on; stays owned by the caller.</param>
    /// <param name="sharedFenceHandle">The consumer's shared fence NT handle, or zero to keep the CPU wait; stays owned
    /// by the caller.</param>
    /// <exception cref="COMException">The device refused the event query the CPU wait needs.</exception>
    public Win32D3D11CompletionSignal(nint device, nint context, nint sharedFenceHandle) {
        m_context = ((ID3D11DeviceContext*)context);
        Order = ((0 == sharedFenceHandle)
            ? new SharedFenceOrder(
                Reason: "no shared fence was offered",
                SharedFence: false
            )
            : TryOpenSharedFence(
                context: ((ID3D11DeviceContext*)context),
                context4: out m_context4,
                device: ((ID3D11Device*)device),
                fence: out m_fence,
                sharedFenceHandle: sharedFenceHandle
            ));

        if (Order.SharedFence) {
            return;
        }

        var description = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT };
        ID3D11Query* query = null;

        ((ID3D11Device*)device)->CreateQuery(
            pQueryDesc: &description,
            ppQuery: &query
        );
        m_query = query;
    }

    /// <summary>Gets how this signal orders the producer's writes: through the shared fence, or by the CPU wait and
    /// why.</summary>
    public SharedFenceOrder Order { get; }

    /// <summary>Completes the writes recorded on the immediate context so far for another device to read: signals the
    /// shared fence's next value and flushes, or waits on the CPU until they have finished.</summary>
    /// <returns>The fence value the writes signal, which the consumer's submission waits for; zero when they finished
    /// before this call returned.</returns>
    /// <exception cref="ObjectDisposedException">The signal was disposed.</exception>
    /// <exception cref="COMException">The device was removed.</exception>
    public ulong Complete() => Complete(value: (m_lastValue + 1UL));
    /// <summary>Completes the writes recorded on the immediate context so far for another device to read, signalling a
    /// value another counter hands out: the ring the writes land in, when more than one producer signals its fence over
    /// time (<see cref="LatestSlotPublication.NextFenceValue"/>).</summary>
    /// <param name="value">The value the fence is set to; greater than every value the fence was set to before.</param>
    /// <returns><paramref name="value"/>, which the consumer's submission waits for; zero when the writes finished before
    /// this call returned.</returns>
    /// <exception cref="ObjectDisposedException">The signal was disposed.</exception>
    /// <exception cref="COMException">The device was removed.</exception>
    public ulong Complete(ulong value) {
        if (m_fence is not null) {
            if (OperatingSystem.IsWindowsVersionAtLeast(
                major: 10,
                minor: 0,
                build: Windows10CreatorsUpdateBuild
            )) {
                m_lastValue = value;
                m_context4->Signal(
                    Value: value,
                    pFence: m_fence
                );
                m_context->Flush();

                return value;
            }
        }

        ObjectDisposedException.ThrowIf(
            condition: (m_query is null),
            instance: this
        );
        WaitForCompletion(
            context: m_context,
            query: m_query
        );

        return 0UL;
    }
    /// <inheritdoc/>
    public void Dispose() {
        Release(value: m_query);
        m_query = null;
        Release(value: m_fence);
        m_fence = null;
        Release(value: m_context4);
        m_context4 = null;
    }

    // Ends the event query, flushes, and spins until everything submitted before End has completed: GetData writes the
    // BOOL only on S_OK and leaves it untouched while pending (S_FALSE); a device-removal HRESULT throws out of the
    // generated wrapper. Issued on a producer's thread at its cadence, never on the render thread.
    private static void WaitForCompletion(ID3D11DeviceContext* context, ID3D11Query* query) {
        context->End(pAsync: ((ID3D11Asynchronous*)query));
        context->Flush();

        BOOL done = false;

        while (!done) {
            context->GetData(
                DataSize: ((uint)sizeof(BOOL)),
                GetDataFlags: 0,
                pAsync: ((ID3D11Asynchronous*)query),
                pData: &done
            );

            if (!done) {
                Thread.SpinWait(iterations: 64);
            }
        }
    }

    // Opens a Direct3D 12 shared fence on a Direct3D 11 device, with the immediate context's ID3D11DeviceContext4 that
    // signals and waits on it; both pointers are owned by the caller on success and null on refusal.
    internal static SharedFenceOrder TryOpenSharedFence(ID3D11Device* device, ID3D11DeviceContext* context, nint sharedFenceHandle, out ID3D11DeviceContext4* context4, out ID3D11Fence* fence) {
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

        var device5Iid = ID3D11Device5.IID_Guid;

        if (((IUnknown*)device)->QueryInterface(
            ppvObject: out var device5,
            riid: in device5Iid
        ).Failed) {
            return new SharedFenceOrder(
                Reason: "the Direct3D 11 device has no ID3D11Device5",
                SharedFence: false
            );
        }

        try {
            var context4Iid = ID3D11DeviceContext4.IID_Guid;

            if (((IUnknown*)context)->QueryInterface(
                ppvObject: out var queried,
                riid: in context4Iid
            ).Failed) {
                return new SharedFenceOrder(
                    Reason: "the Direct3D 11 immediate context has no ID3D11DeviceContext4",
                    SharedFence: false
                );
            }

            void* opened = null;
            var fenceIid = ID3D11Fence.IID_Guid;

            try {
                ((ID3D11Device5*)device5)->OpenSharedFence(
                    hFence: new HANDLE(value: ((void*)sharedFenceHandle)),
                    ReturnedInterface: &fenceIid,
                    ppFence: &opened
                );
            } catch (Exception exception) when ((exception is COMException or ArgumentException or UnauthorizedAccessException)) {
                // The HRESULT mapping throws a handle that is no fence (E_INVALIDARG) as an ArgumentException and one
                // this process may not open (E_ACCESSDENIED) as an UnauthorizedAccessException; each is the device
                // refusing the fence, not a fault of the caller.
                _ = ((IUnknown*)queried)->Release();

                return new SharedFenceOrder(
                    Reason: $"ID3D11Device5::OpenSharedFence refused the fence (0x{exception.HResult:X8})",
                    SharedFence: false
                );
            }

            context4 = ((ID3D11DeviceContext4*)queried);
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
        Order = Win32D3D11CompletionSignal.TryOpenSharedFence(
            context: ((ID3D11DeviceContext*)context),
            context4: out m_context4,
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
