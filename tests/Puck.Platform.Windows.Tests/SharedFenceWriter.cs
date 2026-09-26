using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.Platform.Windows.Tests;

/// <summary>A Direct3D 11 producer for the shared-fence laws: a device on one hardware adapter or the software (WARP)
/// renderer, which opens a consumer's shared texture and writes a pattern into it by a GPU copy. It never waits on the
/// CPU; only a <see cref="Win32D3D11CompletionSignal"/> over its device orders its writes.</summary>
[SupportedOSPlatform("windows10.0.15063")]
internal sealed unsafe class SharedFenceWriter : IDisposable {
    private readonly ID3D11DeviceContext* m_context;
    private readonly ID3D11Device* m_device;
    private readonly ID3D11Device1* m_device1;
    private readonly List<nint> m_textures = [];

    private SharedFenceWriter(ID3D11Device* device, ID3D11Device1* device1, ID3D11DeviceContext* context, long adapterLuid) {
        m_context = context;
        m_device = device;
        m_device1 = device1;
        AdapterLuid = adapterLuid;
    }

    /// <summary>Gets the LUID of the adapter the device is on, packed <c>(HighPart &lt;&lt; 32) | LowPart</c>.</summary>
    public long AdapterLuid { get; }
    /// <summary>Gets the immediate context, as the completion signal takes it.</summary>
    public nint Context => ((nint)m_context);
    /// <summary>Gets the device, as the completion signal takes it.</summary>
    public nint Device => ((nint)m_device);

    /// <summary>Creates a writer on the first hardware adapter, or on WARP; returns <see langword="null"/> when the host
    /// has no such device.</summary>
    /// <param name="warp">Whether the device is the software renderer.</param>
    /// <returns>The writer, owned by the caller, or <see langword="null"/>.</returns>
    public static SharedFenceWriter? TryCreate(bool warp) {
        IDXGIAdapter1* adapter = null;

        if (!warp) {
            adapter = FirstHardwareAdapter();

            if (adapter is null) {
                return null;
            }
        }

        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;

        try {
            D3D_FEATURE_LEVEL granted;
            ReadOnlySpan<D3D_FEATURE_LEVEL> levels = [D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0];
            using var noSoftwareModule = new SafeFileHandle(
                ownsHandle: false,
                preexistingHandle: 0
            );
            var result = PInvoke.D3D11CreateDevice(
                DriverType: (warp
                    ? D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP
                    : D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN),
                Flags: D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                SDKVersion: PInvoke.D3D11_SDK_VERSION,
                Software: noSoftwareModule,
                pAdapter: ((IDXGIAdapter*)adapter),
                pFeatureLevel: &granted,
                pFeatureLevels: levels,
                ppDevice: &device,
                ppImmediateContext: &context
            );

            if (result.Failed) {
                Release(value: context);
                Release(value: device);

                return null;
            }

            var device1Iid = ID3D11Device1.IID_Guid;

            Marshal.ThrowExceptionForHR(errorCode: ((IUnknown*)device)->QueryInterface(
                ppvObject: out var device1,
                riid: in device1Iid
            ).Value);

            return new SharedFenceWriter(
                adapterLuid: AdapterLuidOf(device: device),
                context: context,
                device: device,
                device1: ((ID3D11Device1*)device1)
            );
        } catch {
            Release(value: context);
            Release(value: device);

            throw;
        } finally {
            Release(value: adapter);
        }
    }

    /// <summary>Opens a consumer's shared texture on this device.</summary>
    /// <param name="sharedHandle">The texture's shared NT handle.</param>
    /// <returns>The opened <c>ID3D11Texture2D*</c>, released with the writer.</returns>
    public nint Open(nint sharedHandle) {
        using var handle = new SafeFileHandle(
            ownsHandle: false,
            preexistingHandle: sharedHandle
        );

        m_device1->OpenSharedResource1(
            hResource: handle,
            ppResource: out var texture,
            returnedInterface: ID3D11Texture2D.IID_Guid
        );
        m_textures.Add(item: ((nint)texture));

        return ((nint)texture);
    }
    /// <summary>Records a GPU copy of an RGBA8 pattern into an opened texture on the immediate context; nothing waits
    /// for it.</summary>
    /// <param name="target">The opened texture.</param>
    /// <param name="pixels">The tightly packed RGBA8 pattern.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    public void Write(nint target, byte[] pixels, int width, int height) {
        var description = new D3D11_TEXTURE2D_DESC {
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            Height = ((uint)height),
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            Width = ((uint)width),
        };
        ID3D11Texture2D* source = null;

        fixed (byte* data = pixels) {
            var initialData = new D3D11_SUBRESOURCE_DATA {
                SysMemPitch = ((uint)(width * 4)),
                pSysMem = data,
            };

            m_device->CreateTexture2D(
                pDesc: &description,
                pInitialData: &initialData,
                ppTexture2D: &source
            );
        }

        try {
            m_context->CopyResource(
                pDstResource: ((ID3D11Resource*)target),
                pSrcResource: ((ID3D11Resource*)source)
            );
        } finally {
            Release(value: source);
        }
    }
    public void Dispose() {
        foreach (var texture in m_textures) {
            Release(value: ((IUnknown*)texture));
        }

        Release(value: m_device1);
        Release(value: m_context);
        Release(value: m_device);
    }

    private static long AdapterLuidOf(ID3D11Device* device) {
        var dxgiIid = IDXGIDevice.IID_Guid;

        Marshal.ThrowExceptionForHR(errorCode: ((IUnknown*)device)->QueryInterface(
            ppvObject: out var dxgiDevice,
            riid: in dxgiIid
        ).Value);

        IDXGIAdapter* adapter = null;

        try {
            ((IDXGIDevice*)dxgiDevice)->GetAdapter(pAdapter: &adapter);

            var description = adapter->GetDesc();

            return ((((long)description.AdapterLuid.HighPart) << 32) | description.AdapterLuid.LowPart);
        } finally {
            Release(value: adapter);
            Release(value: ((IUnknown*)dxgiDevice));
        }
    }
    private static IDXGIAdapter1* FirstHardwareAdapter() {
        Marshal.ThrowExceptionForHR(errorCode: PInvoke.CreateDXGIFactory1(
            ppFactory: out var factoryPointer,
            riid: IDXGIFactory1.IID_Guid
        ).Value);

        var factory = ((IDXGIFactory1*)factoryPointer);

        try {
            for (var index = 0u; ; index++) {
                IDXGIAdapter1* adapter;
                var result = factory->EnumAdapters1(
                    Adapter: index,
                    ppAdapter: &adapter
                );

                if (HRESULT.DXGI_ERROR_NOT_FOUND == result) {
                    return null;
                }

                Marshal.ThrowExceptionForHR(errorCode: result.Value);

                if (0 == (adapter->GetDesc1().Flags & DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE)) {
                    return adapter;
                }

                Release(value: adapter);
            }
        } finally {
            Release(value: factory);
        }
    }
    private static void Release<T>(T* value) where T : unmanaged {
        if (value is not null) {
            _ = ((IUnknown*)value)->Release();
        }
    }
}
