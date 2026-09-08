using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
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
internal sealed unsafe class Win32D3D11VideoDevice : IDisposable, IProbeKernelDevice {
    private ID3D11DeviceContext* m_context;
    private ID3D11Device* m_device;
    private ID3D11Device1* m_device1;
    private bool m_disposed;
    private ID3D10Multithread* m_multithread;
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
                adapter: ((IDXGIAdapter*)adapter),
                context: out var context,
                device: out var device,
                driverType: D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                flags: D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT
            );

            m_context = context;
            m_device = device;

            // ID3D11Device1 carries OpenSharedResource1 (the NT-handle open).
            var device1Iid = ID3D11Device1.IID_Guid;

            Win32D3D11.ThrowIfFailed(hr: ((IUnknown*)device)->QueryInterface(ppvObject: out var device1, riid: in device1Iid), operation: "QueryInterface(ID3D11Device1)");
            m_device1 = ((ID3D11Device1*)device1);

            var multithreadIid = ID3D10Multithread.IID_Guid;

            Win32D3D11.ThrowIfFailed(hr: ((IUnknown*)device)->QueryInterface(ppvObject: out var multithread, riid: in multithreadIid), operation: "QueryInterface(ID3D10Multithread)");
            m_multithread = ((ID3D10Multithread*)multithread);

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
    public nint DevicePointer => ((nint)m_device);
    public ID3D11DeviceContext* Context => m_context;
    public ID3D11Device* Device => m_device;
    public ID3D11Device1* Device1 => m_device1;

    /// <summary>Holds the device's critical section across a multi-call sequence on its immediate context (Media
    /// Foundation's transforms share the device).</summary>
    public void Enter() => m_multithread->Enter();
    public void Leave() => m_multithread->Leave();
    /// <summary>Copies a decoded frame into a shared target and blocks (on the calling grabber thread) until the copy
    /// has completed on the GPU — so the target may be published for another device to sample.</summary>
    /// <param name="targetTexture">The shared target texture (an <c>ID3D11Texture2D*</c> from <see cref="OpenSharedTexture"/>).</param>
    /// <param name="sourceTexture">The frame's texture (an <c>ID3D11Texture2D*</c>, e.g. from <c>IMFDXGIBuffer</c>).</param>
    /// <param name="sourceSubresource">The source array slice (DXVA components output texture arrays).</param>
    public void CopyToTarget(nint targetTexture, nint sourceTexture, uint sourceSubresource) {
        var context = m_context;

        context->CopySubresourceRegion(
            DstSubresource: 0,
            DstX: 0,
            DstY: 0,
            DstZ: 0,
            SrcSubresource: sourceSubresource,
            pDstResource: ((ID3D11Resource*)targetTexture),
            pSrcBox: null,
            pSrcResource: ((ID3D11Resource*)sourceTexture)
        );
        Win32D3D11.WaitForCompletion(context: context, query: m_query);
    }
    /// <summary>Opens a consumer-provisioned shared texture (an NT handle) on this device; the caller owns the returned
    /// <c>ID3D11Texture2D*</c> and must release it via <see cref="ReleaseTexture"/>.</summary>
    /// <param name="sharedHandle">The shared NT handle of the texture to open.</param>
    /// <returns>The opened texture pointer.</returns>
    public nint OpenSharedTexture(nint sharedHandle) {
        // A non-owning SafeHandle wrapper: the NT handle belongs to the consumer's exportable texture. The generated
        // wrapper throws on failure (the raw HRESULT rides in the exception).
        using var handle = new SafeFileHandle(ownsHandle: false, preexistingHandle: sharedHandle);

        m_device1->OpenSharedResource1(
            hResource: handle,
            ppResource: out var texture,
            returnedInterface: ID3D11Texture2D.IID_Guid
        );

        return ((nint)texture);
    }
    /// <summary>Creates a shader-resource view over an opened target so a hosted kernel can sample it; the caller
    /// releases it via <see cref="ReleaseTexture"/>.</summary>
    /// <param name="texture">The <c>ID3D11Texture2D*</c> to view.</param>
    /// <returns>The <c>ID3D11ShaderResourceView*</c>.</returns>
    public nint CreateShaderResourceView(nint texture) {
        ID3D11ShaderResourceView* view = null;

        m_device->CreateShaderResourceView(pDesc: null, pResource: ((ID3D11Resource*)texture), ppSRView: &view);

        return ((nint)view);
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

        if (m_multithread is not null) {
            _ = ((IUnknown*)m_multithread)->Release();
            m_multithread = null;
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
