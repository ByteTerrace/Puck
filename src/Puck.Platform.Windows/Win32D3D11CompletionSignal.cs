using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.System.Com;

namespace Puck.Platform.Windows;

/// <summary>
/// Orders a Direct3D 11 producer's writes into shared targets before a consumer on another device reads them. With the
/// consumer's shared fence (a Direct3D 12 fence created with <c>D3D12_FENCE_FLAG_SHARED</c>) opened through
/// <c>ID3D11Device5::OpenSharedFence</c>, <see cref="Complete"/> signals the next value on the immediate context and
/// returns it, and the consumer's submission waits for that value on the GPU. A device that cannot open the fence, or a
/// producer offered none, keeps the CPU wait: <see cref="Complete"/> spins on an event query until the write has
/// finished and returns zero. <see cref="Order"/> says which. Affine to the producer's thread; the caller holds the
/// device's critical section when the context is shared.
/// </summary>
[SupportedOSPlatform("windows8.0")]
public sealed unsafe class Win32D3D11CompletionSignal : IDisposable {
    private const int Windows10CreatorsUpdateBuild = 15063;

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
            : TryOpenFence(
                context: ((ID3D11DeviceContext*)context),
                device: ((ID3D11Device*)device),
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
    public ulong Complete() {
        if (m_fence is not null) {
            if (OperatingSystem.IsWindowsVersionAtLeast(
                major: 10,
                minor: 0,
                build: Windows10CreatorsUpdateBuild
            )) {
                var value = ++m_lastValue;

                m_context4->Signal(
                    pFence: m_fence,
                    Value: value
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
    private SharedFenceOrder TryOpenFence(ID3D11Device* device, ID3D11DeviceContext* context, nint sharedFenceHandle) {
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
                ppvObject: out var context4,
                riid: in context4Iid
            ).Failed) {
                return new SharedFenceOrder(
                    Reason: "the Direct3D 11 immediate context has no ID3D11DeviceContext4",
                    SharedFence: false
                );
            }

            void* fence = null;
            var fenceIid = ID3D11Fence.IID_Guid;

            try {
                ((ID3D11Device5*)device5)->OpenSharedFence(
                    hFence: new HANDLE(value: ((void*)sharedFenceHandle)),
                    ReturnedInterface: &fenceIid,
                    ppFence: &fence
                );
            } catch (COMException exception) {
                _ = ((IUnknown*)context4)->Release();

                return new SharedFenceOrder(
                    Reason: $"ID3D11Device5::OpenSharedFence refused the fence (0x{exception.HResult:X8})",
                    SharedFence: false
                );
            }

            m_context4 = ((ID3D11DeviceContext4*)context4);
            m_fence = ((ID3D11Fence*)fence);

            return new SharedFenceOrder(
                Reason: "",
                SharedFence: true
            );
        } finally {
            _ = ((IUnknown*)device5)->Release();
        }
    }
    private static void Release<T>(T* value) where T : unmanaged {
        if (value is not null) {
            _ = ((IUnknown*)value)->Release();
        }
    }
}
