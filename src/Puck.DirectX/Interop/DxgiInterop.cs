using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Graphics.Dxgi;

namespace Puck.DirectX.Interop;

/// <summary>
/// Low-level DXGI helpers shared by the native APIs: creating a factory.
/// </summary>
[SupportedOSPlatform("windows8.1")]
internal static unsafe class DxgiInterop {
    /// <summary>Creates an <c>IDXGIFactory4</c>; the caller owns the returned pointer and must <c>Release</c> it.</summary>
    /// <returns>A pointer to the newly created factory.</returns>
    /// <exception cref="DirectXException"><c>CreateDXGIFactory2</c> failed.</exception>
    public static IDXGIFactory4* CreateFactory() {
        var result = PInvoke.CreateDXGIFactory2(
            Flags: default,
            ppFactory: out IDXGIFactory4* factory
        );

        result.ThrowIfFailed(operation: "CreateDXGIFactory2");
        return factory;
    }
}
