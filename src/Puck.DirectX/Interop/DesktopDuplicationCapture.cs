using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// Live screen capture via the DXGI Desktop Duplication API. A dedicated Direct3D 11 device (created on
/// <paramref name="adapterLuid"/>'s GPU) duplicates the primary output; each <see cref="CaptureFrame"/> acquires the
/// latest desktop image (draining blank/metadata-only frames until a real present) and <c>CopyResource</c>s it into a
/// BGRA capture texture on the GPU — and, unlike a GDI <c>StretchBlt</c>, it captures hardware-composited and
/// protected content. <see cref="ReadbackBgra"/> maps a staging copy for verification (a gate's PNG).
/// <para>
/// Zero-copy hand-off to a Direct3D 12 host (sampling the desktop straight into a viewport pane) is NOT done here: a
/// D3D11-created NT-handle shared texture forces a keyed mutex the D3D12 consumer cannot acquire, and D3D11
/// <c>OpenSharedResource1</c> cannot open a D3D12-created resource (sharing only flows D3D11→D3D12). The supported
/// path is <c>D3D11On12</c> — create the D3D11 device over the host's D3D12 device and capture into a
/// <c>CreateWrappedResource</c>'d host texture.
/// </para>
/// The desktop is BGRA (<see cref="Format"/>). Single-thread affine.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DesktopDuplicationCapture : IDisposable {
    private const uint AcquireTimeoutMilliseconds = 500;

    private static readonly HRESULT DxgiErrorWaitTimeout = new(unchecked((int)0x887A0027));
    private static readonly HRESULT SFalse = new(1);

    private long m_adapterLuid;
    private string m_adapterDescription = "";
    private ID3D11DeviceContext* m_context;
    private ID3D11Query* m_copyQuery;
    private ID3D11Device* m_device;
    private bool m_disposed;
    private IDXGIOutputDuplication* m_duplication;
    private uint m_height;
    private ID3D11Texture2D* m_sharedTexture;
    private ID3D11Texture2D* m_stagingTexture;
    private uint m_width;

    /// <summary>Initializes a new instance of the <see cref="DesktopDuplicationCapture"/> class, duplicating the
    /// primary output.</summary>
    /// <param name="adapterLuid">The packed LUID of the adapter to create the Direct3D 11 device on — pass the
    /// consuming Direct3D 12 host's LUID so the shared handle opens on the same GPU; 0 uses the default hardware adapter.</param>
    /// <exception cref="DirectXException">A Direct3D 11 / DXGI call failed (e.g. Desktop Duplication is unavailable).</exception>
    public DesktopDuplicationCapture(long adapterLuid = 0) {
        Initialize(adapterLuid: adapterLuid);
    }

    /// <summary>Gets the human-readable description of the adapter the capture device was created on.</summary>
    public string AdapterDescription => m_adapterDescription;
    /// <summary>Gets the packed LUID of the adapter the capture device was created on.</summary>
    public long AdapterLuid => m_adapterLuid;
    /// <summary>Gets the BGRA pixel format of the captured texture (the desktop format).</summary>
    public uint Format => GpuPixelFormat.B8G8R8A8Unorm;
    /// <summary>Gets the captured texture height in pixels (the primary output's height).</summary>
    public uint Height => m_height;
    /// <summary>Gets the captured texture width in pixels (the primary output's width).</summary>
    public uint Width => m_width;

    private static void Release(ref nint pointer) {
        if (0 != pointer) {
            _ = ((IUnknown*)pointer)->Release();
            pointer = 0;
        }
    }

    // The CsWin32 friendly instance methods for these calls return void and throw on any failing HRESULT. Desktop
    // Duplication's AcquireNextFrame returns the benign DXGI_ERROR_WAIT_TIMEOUT and the event query's GetData returns
    // S_FALSE while the copy is still in flight — both are codes this code must observe rather than throw on — so the
    // raw COM vtable slot is invoked directly to recover the HRESULT (AcquireNextFrame is slot 8, GetData is slot 29).
    private static HRESULT AcquireNextFrameRaw(IDXGIOutputDuplication* duplication, uint timeoutMilliseconds, DXGI_OUTDUPL_FRAME_INFO* frameInfo, IDXGIResource** desktopResource) {
        var vtable = *(void***)duplication;

        return ((delegate* unmanaged[Stdcall]<IDXGIOutputDuplication*, uint, DXGI_OUTDUPL_FRAME_INFO*, IDXGIResource**, HRESULT>)vtable[8])(duplication, timeoutMilliseconds, frameInfo, desktopResource);
    }

    private static HRESULT GetDataRaw(ID3D11DeviceContext* context, ID3D11Asynchronous* async, void* pData, uint DataSize, uint GetDataFlags) {
        var vtable = *(void***)context;

        return ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, ID3D11Asynchronous*, void*, uint, uint, HRESULT>)vtable[29])(context, async, pData, DataSize, GetDataFlags);
    }

    private static void* QueryInterface(void* unknown, in System.Guid iid) {
        void* result;
        var guid = iid;

        ((IUnknown*)unknown)->QueryInterface(&guid, &result).ThrowOnFailure();

        return result;
    }

    private void Initialize(long adapterLuid) {
        // 1. A Direct3D 11 device. When a LUID is given, create it on THAT adapter (the consuming D3D12 host's GPU) so
        //    the cross-API shared handle opens; otherwise the default hardware adapter. A specific adapter requires the
        //    UNKNOWN driver type.
        ID3D11Device* device;
        ID3D11DeviceContext* context;

        if (0 != adapterLuid) {
            IDXGIFactory4* factory;
            var factoryIid = IDXGIFactory4.IID_Guid;

            PInvoke.CreateDXGIFactory2(0, &factoryIid, (void**)&factory).ThrowOnFailure();

            try {
                var luid = new Windows.Win32.Foundation.LUID {
                    HighPart = (int)(adapterLuid >> 32),
                    LowPart = (uint)(adapterLuid & 0xFFFFFFFFL),
                };

                void* adapterPtr;
                var adapterIid = IDXGIAdapter.IID_Guid;

                factory->EnumAdapterByLuid(luid, &adapterIid, &adapterPtr);

                var adapter = (IDXGIAdapter*)adapterPtr;

                try {
                    PInvoke.D3D11CreateDevice((IDXGIAdapter*)adapter, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, default, D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, null, 0, 7, &device, null, &context).ThrowOnFailure();
                } finally {
                    _ = ((IUnknown*)adapter)->Release();
                }
            } finally {
                _ = ((IUnknown*)factory)->Release();
            }
        } else {
            PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default, D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, null, 0, 7, &device, null, &context).ThrowOnFailure();
        }

        m_context = context;
        m_device = device;

        // 2. Reach the adapter's primary output and duplicate it: device -> IDXGIDevice -> adapter -> output 0 ->
        //    IDXGIOutput1 -> DuplicateOutput.
        var dxgiDevice = (IDXGIDevice*)QueryInterface(unknown: device, iid: IDXGIDevice.IID_Guid);

        try {
            IDXGIAdapter* adapter;

            dxgiDevice->GetAdapter(&adapter);

            var adapterDesc = adapter->GetDesc();
            m_adapterLuid = (((long)adapterDesc.AdapterLuid.HighPart << 32) | (uint)adapterDesc.AdapterLuid.LowPart);
            m_adapterDescription = adapterDesc.Description.ToString();

            try {
                IDXGIOutput* output;

                adapter->EnumOutputs(0, &output).ThrowOnFailure();

                var output1 = (IDXGIOutput1*)QueryInterface(unknown: output, iid: IDXGIOutput1.IID_Guid);

                _ = ((IUnknown*)output)->Release();

                try {
                    IDXGIOutputDuplication* duplication;

                    // DuplicateOutput is a CsWin32 friendly method (void; throws on failure) — so a secure/locked
                    // desktop (E_ACCESSDENIED), an unsupported hybrid-GPU adapter, or the duplication limit surfaces as
                    // a catchable DirectXException the gate reports as a clean infra-fail, not a null-deref.
                    output1->DuplicateOutput((IUnknown*)device, &duplication);
                    m_duplication = duplication;
                } finally {
                    _ = ((IUnknown*)output1)->Release();
                }
            } finally {
                _ = ((IUnknown*)adapter)->Release();
            }
        } finally {
            _ = ((IUnknown*)dxgiDevice)->Release();
        }

        DXGI_OUTDUPL_DESC duplicationDesc;

        m_duplication->GetDesc(&duplicationDesc);
        m_height = duplicationDesc.ModeDesc.Height;
        m_width = duplicationDesc.ModeDesc.Width;

        // 3. A BGRA capture texture the desktop frame is copied into each capture. (The zero-copy D3D12-import path
        //    layers a shared NT-handle texture on top of this; the verification gate reads this one back via staging.)
        var textureDesc = new D3D11_TEXTURE2D_DESC {
            ArraySize = 1,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = 0,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            Height = m_height,
            MipLevels = 1,
            MiscFlags = 0,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0, },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            Width = m_width,
        };

        ID3D11Texture2D* sharedTexture;

        m_device->CreateTexture2D(in textureDesc, null, &sharedTexture);
        m_sharedTexture = sharedTexture;

        // 4. An EVENT query to CPU-order each copy before the read.
        var queryDesc = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT, };
        ID3D11Query* copyQuery;

        m_device->CreateQuery(in queryDesc, &copyQuery);
        m_copyQuery = copyQuery;
    }

    /// <summary>Acquires the latest desktop frame and copies it into the shared texture, blocking until the copy is
    /// complete on the GPU. Retries across the brief windows where Desktop Duplication has no new frame.</summary>
    /// <returns><see langword="true"/> if a frame was captured; <see langword="false"/> if none arrived (a static
    /// desktop with no updates within the retry budget).</returns>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    /// <exception cref="DirectXException">A Direct3D 11 / DXGI call failed (other than a benign timeout).</exception>
    public bool CaptureFrame() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        var captured = false;
        var realPresent = false;

        // The first AcquireNextFrame(s) after DuplicateOutput can hand back blank / metadata-only frames; drain until a
        // real desktop PRESENT (LastPresentTime > 0), copying each acquired frame, so the capture texture ends up with
        // actual content. A WAIT_TIMEOUT means no new frame this attempt — keep trying within the budget.
        for (var attempt = 0; ((attempt < 64) && !realPresent); attempt++) {
            DXGI_OUTDUPL_FRAME_INFO frameInfo;
            IDXGIResource* desktopResource;
            var result = AcquireNextFrameRaw(m_duplication, AcquireTimeoutMilliseconds, &frameInfo, &desktopResource);

            if (result == DxgiErrorWaitTimeout) {
                continue;
            }

            result.ThrowOnFailure();

            try {
                var desktopTexture = (ID3D11Texture2D*)QueryInterface(unknown: desktopResource, iid: ID3D11Texture2D.IID_Guid);

                try {
                    m_context->CopyResource((ID3D11Resource*)m_sharedTexture, (ID3D11Resource*)desktopTexture);

                    // CPU-wait for the copy so a later read sees a complete frame.
                    m_context->End((ID3D11Asynchronous*)m_copyQuery);
                    m_context->Flush();

                    while (GetDataRaw(m_context, (ID3D11Asynchronous*)m_copyQuery, pData: null, DataSize: 0, GetDataFlags: 0) == SFalse) {
                        Thread.Yield();
                    }

                    captured = true;
                    realPresent = (frameInfo.LastPresentTime > 0);
                } finally {
                    _ = ((IUnknown*)desktopTexture)->Release();
                }
            } finally {
                _ = ((IUnknown*)desktopResource)->Release();
                m_duplication->ReleaseFrame();
            }
        }

        return captured;
    }

    /// <summary>Copies the captured texture into a CPU-readable staging texture and returns its tightly packed,
    /// top-down BGRA pixels. For verification / a gate's PNG only — the live runtime path imports the shared handle on
    /// the GPU (zero-copy) rather than reading back to the CPU.</summary>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    public byte[] ReadbackBgra() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        if (m_stagingTexture is null) {
            var stagingDesc = new D3D11_TEXTURE2D_DESC {
                ArraySize = 1,
                BindFlags = 0,
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                Height = m_height,
                MipLevels = 1,
                MiscFlags = 0,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0, },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                Width = m_width,
            };

            ID3D11Texture2D* staging;

            m_device->CreateTexture2D(in stagingDesc, null, &staging);
            m_stagingTexture = staging;
        }

        m_context->CopyResource((ID3D11Resource*)m_stagingTexture, (ID3D11Resource*)m_sharedTexture);

        D3D11_MAPPED_SUBRESOURCE mapped;

        m_context->Map((ID3D11Resource*)m_stagingTexture, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);

        var rowBytes = checked((int)(m_width * 4));
        var pixels = new byte[rowBytes * (int)m_height];

        try {
            var source = (byte*)mapped.pData;

            for (var row = 0; (row < (int)m_height); row++) {
                new ReadOnlySpan<byte>(pointer: (source + (row * (int)mapped.RowPitch)), length: rowBytes)
                    .CopyTo(destination: pixels.AsSpan(start: (row * rowBytes), length: rowBytes));
            }
        } finally {
            m_context->Unmap((ID3D11Resource*)m_stagingTexture, 0);
        }

        return pixels;
    }

    /// <summary>Releases the duplication, the shared + staging textures and the handle, the query, and the Direct3D 11 device.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        var copyQuery = (nint)m_copyQuery;
        var duplication = (nint)m_duplication;
        var sharedTexture = (nint)m_sharedTexture;
        var stagingTexture = (nint)m_stagingTexture;

        Release(pointer: ref copyQuery);
        Release(pointer: ref duplication);
        Release(pointer: ref sharedTexture);
        Release(pointer: ref stagingTexture);

        m_copyQuery = (ID3D11Query*)copyQuery;
        m_duplication = (IDXGIOutputDuplication*)duplication;
        m_sharedTexture = (ID3D11Texture2D*)sharedTexture;
        m_stagingTexture = (ID3D11Texture2D*)stagingTexture;

        var context = (nint)m_context;
        var device = (nint)m_device;

        Release(pointer: ref context);
        Release(pointer: ref device);

        m_context = (ID3D11DeviceContext*)context;
        m_device = (ID3D11Device*)device;
    }
}
