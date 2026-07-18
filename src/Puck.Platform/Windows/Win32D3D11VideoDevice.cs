using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.Com;

namespace Puck.Platform.Windows;

/// <summary>
/// The camera GPU tier's Direct3D 11 video device: created on the adapter named by LUID (so its textures share the
/// consumer render device's GPU), with video support (the DXVA decoder/processor Media Foundation drives) and
/// multithread protection (Media Foundation's worker threads share the device). It owns the small GPU toolbox the
/// zero-copy path needs — opening consumer-provisioned shared textures (<see cref="OpenSharedTexture"/>) and copying a
/// decoded frame into one with completion (<see cref="CopyToTarget"/>: copy + flush + an event-query CPU wait, issued on
/// the camera's grabber thread at camera cadence, never the render thread). All members are single-thread affine to
/// that grabber thread.
/// </summary>
[SupportedOSPlatform("windows8.0")]
internal sealed unsafe class Win32D3D11VideoDevice : IDisposable {
    private ID3D11DeviceContext* m_context;
    private ID3D11Device* m_device;
    private ID3D11Device1* m_device1;
    private bool m_disposed;
    private ID3D11Query* m_query;

    /// <summary>Initializes a new instance of the <see cref="Win32D3D11VideoDevice"/> class on the LUID-named adapter.</summary>
    /// <param name="adapterLuid">The adapter LUID the consumer render device reported (packed <c>(HighPart &lt;&lt; 32) | LowPart</c>).</param>
    /// <exception cref="InvalidOperationException">No adapter matches, or device creation failed.</exception>
    public Win32D3D11VideoDevice(long adapterLuid) {
        var adapter = Win32D3D11.FindAdapterByLuid(adapterLuid: adapterLuid);

        if (adapter is null) {
            throw new InvalidOperationException(message: $"no DXGI adapter was found with LUID 0x{adapterLuid:X16}");
        }

        try {
            // VIDEO_SUPPORT: Media Foundation's DXVA components require it. BGRA_SUPPORT: the processor's ARGB32
            // output format. Driver type must be UNKNOWN when an explicit adapter is passed.
            Win32D3D11.CreateMultithreadedDevice(
                adapter: (IDXGIAdapter*)adapter,
                driverType: D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                flags: D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                device: out var device,
                context: out var context
            );

            m_context = context;
            m_device = device;

            // ID3D11Device1 carries OpenSharedResource1 (the NT-handle open).
            var device1Iid = ID3D11Device1.IID_Guid;

            Win32D3D11.ThrowIfFailed(hr: ((IUnknown*)device)->QueryInterface(in device1Iid, out var device1), operation: "QueryInterface(ID3D11Device1)");
            m_device1 = (ID3D11Device1*)device1;

            // The event query CopyToTarget spins on: signaled when everything submitted before End has completed.
            var queryDesc = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT };
            ID3D11Query* query;

            device->CreateQuery(pQueryDesc: &queryDesc, ppQuery: &query);
            m_query = query;
        } finally {
            _ = adapter->Release();
        }
    }

    /// <summary>The device as an <c>IUnknown</c> pointer (for <c>IMFDXGIDeviceManager::ResetDevice</c>).</summary>
    public nint DevicePointer => (nint)m_device;

    /// <summary>Copies a decoded frame into a shared target and blocks (on the calling grabber thread) until the copy
    /// has completed on the GPU — so the target may be published for another device to sample.</summary>
    /// <param name="targetTexture">The shared target texture (an <c>ID3D11Texture2D*</c> from <see cref="OpenSharedTexture"/>).</param>
    /// <param name="sourceTexture">The frame's texture (an <c>ID3D11Texture2D*</c>, e.g. from <c>IMFDXGIBuffer</c>).</param>
    /// <param name="sourceSubresource">The source array slice (DXVA components output texture arrays).</param>
    public void CopyToTarget(nint targetTexture, nint sourceTexture, uint sourceSubresource) {
        var context = m_context;

        context->CopySubresourceRegion(
            pDstResource: (ID3D11Resource*)targetTexture,
            DstSubresource: 0,
            DstX: 0,
            DstY: 0,
            DstZ: 0,
            pSrcResource: (ID3D11Resource*)sourceTexture,
            SrcSubresource: sourceSubresource,
            pSrcBox: null
        );
        context->End(pAsync: (ID3D11Asynchronous*)m_query);
        context->Flush();

        // Spin the event query to completion; at camera cadence (~30 fps) on the grabber thread this is a short,
        // render-pump-invisible wait. GetData writes the BOOL only once everything before End has completed (S_OK);
        // while pending (S_FALSE) it leaves it untouched.
        BOOL done = false;

        while (!done) {
            context->GetData(pAsync: (ID3D11Asynchronous*)m_query, pData: &done, DataSize: (uint)sizeof(BOOL), GetDataFlags: 0);

            if (!done) {
                Thread.SpinWait(iterations: 64);
            }
        }
    }

    /// <summary>Opens a consumer-provisioned shared texture (an NT handle) on this device; the caller owns the returned
    /// <c>ID3D11Texture2D*</c> and must release it via <see cref="ReleaseTexture"/>.</summary>
    /// <param name="sharedHandle">The shared NT handle of the texture to open.</param>
    /// <returns>The opened texture pointer.</returns>
    public nint OpenSharedTexture(nint sharedHandle) {
        // A non-owning SafeHandle wrapper: the NT handle belongs to the consumer's exportable texture. The generated
        // wrapper throws on failure (the raw HRESULT rides in the exception).
        using var handle = new SafeFileHandle(preexistingHandle: sharedHandle, ownsHandle: false);

        m_device1->OpenSharedResource1(
            hResource: handle,
            returnedInterface: ID3D11Texture2D.IID_Guid,
            ppResource: out var texture
        );

        return (nint)texture;
    }

    /// <summary>Releases a COM pointer obtained from this device (an opened shared texture or a frame texture).</summary>
    /// <param name="texture">The texture pointer; zero is ignored.</param>
    public static void ReleaseTexture(nint texture) {
        if (0 != texture) {
            _ = ((IUnknown*)texture)->Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (m_query is not null) {
            _ = ((IUnknown*)m_query)->Release();
            m_query = null;
        }

        if (m_device1 is not null) {
            _ = ((IUnknown*)m_device1)->Release();
            m_device1 = null;
        }

        if (m_context is not null) {
            _ = ((IUnknown*)m_context)->Release();
            m_context = null;
        }

        if (m_device is not null) {
            _ = ((IUnknown*)m_device)->Release();
            m_device = null;
        }
    }

}
