using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.Com;

namespace Puck.Platform.Windows;

// Shared Direct3D 11 device creation for the platform capture feeds. Both the Windows Graphics Capture staging device
// and the camera GPU-tier video device open a feature-level 11_1/11_0 hardware device with multithread protection; the
// caller supplies the adapter (null for the default), driver type, and creation flags.
[SupportedOSPlatform("windows8.0")]
internal static unsafe class Win32D3D11 {
    public static void CreateMultithreadedDevice(IDXGIAdapter* adapter, D3D_DRIVER_TYPE driverType, D3D11_CREATE_DEVICE_FLAG flags, out ID3D11Device* device, out ID3D11DeviceContext* context) {
        ID3D11Device* createdDevice;
        ID3D11DeviceContext* createdContext;
        D3D_FEATURE_LEVEL granted;
        ReadOnlySpan<D3D_FEATURE_LEVEL> levels = [D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0];
        // Software is the NULL HMODULE (no software rasterizer); the generated wrapper wants a non-null SafeHandle.
        using var noSoftwareModule = new SafeFileHandle(ownsHandle: false, preexistingHandle: 0);

        ThrowIfFailed(hr: PInvoke.D3D11CreateDevice(
            DriverType: driverType,
            Flags: flags,
            SDKVersion: PInvoke.D3D11_SDK_VERSION,
            Software: noSoftwareModule,
            pAdapter: adapter,
            pFeatureLevel: &granted,
            pFeatureLevels: levels,
            ppDevice: &createdDevice,
            ppImmediateContext: &createdContext
        ), operation: "D3D11CreateDevice");

        try {
            var multithreadIid = ID3D10Multithread.IID_Guid;

            ThrowIfFailed(hr: ((IUnknown*)createdDevice)->QueryInterface(ppvObject: out var multithread, riid: in multithreadIid), operation: "QueryInterface(ID3D10Multithread)");
            _ = ((ID3D10Multithread*)multithread)->SetMultithreadProtected(bMTProtect: true);
            _ = ((IUnknown*)multithread)->Release();
        } catch {
            _ = ((IUnknown*)createdContext)->Release();
            _ = ((IUnknown*)createdDevice)->Release();
            throw;
        }

        context = createdContext;
        device = createdDevice;
    }
    // Ends the event query, flushes, and spins until everything submitted before End has completed: GetData writes the
    // BOOL only on S_OK and leaves it untouched while pending (S_FALSE); a device-removal HRESULT throws out of the
    // generated wrapper. Issued on a camera worker at camera cadence, never on the render thread.
    public static void WaitForCompletion(ID3D11DeviceContext* context, ID3D11Query* query) {
        context->End(pAsync: ((ID3D11Asynchronous*)query));
        context->Flush();

        BOOL done = false;

        while (!done) {
            context->GetData(DataSize: ((uint)sizeof(BOOL)), GetDataFlags: 0, pAsync: ((ID3D11Asynchronous*)query), pData: &done);

            if (!done) {
                Thread.SpinWait(iterations: 64);
            }
        }
    }
    public static void ThrowIfFailed(HRESULT hr, string operation) {
        if (hr.Value < 0) {
            throw new COMException(errorCode: hr.Value, message: $"{operation} failed");
        }
    }
    // Returns an owned adapter pointer the caller must Release, or null when no adapter carries the LUID. Shared by the
    // capture feeds that must create their D3D11 device on a specific adapter (packed (HighPart << 32) | LowPart) so its
    // shared textures can be opened by a render device on the same adapter.
    public static IDXGIAdapter1* FindAdapterByLuid(long adapterLuid) {
        ThrowIfFailed(hr: PInvoke.CreateDXGIFactory1(ppFactory: out var factoryPointer, riid: IDXGIFactory1.IID_Guid), operation: "CreateDXGIFactory1");

        var factory = ((IDXGIFactory1*)factoryPointer);

        try {
            for (var index = 0u; ; index++) {
                IDXGIAdapter1* adapter;
                var hr = factory->EnumAdapters1(Adapter: index, ppAdapter: &adapter);

                if (HRESULT.DXGI_ERROR_NOT_FOUND == hr) {
                    return null;
                }

                ThrowIfFailed(hr: hr, operation: "IDXGIFactory1::EnumAdapters1");

                var description = adapter->GetDesc1();
                var luid = (((long)description.AdapterLuid.HighPart) << 32) | description.AdapterLuid.LowPart;

                if (luid == adapterLuid) {
                    return adapter;
                }

                _ = adapter->Release();
            }
        } finally {
            _ = factory->Release();
        }
    }
}
