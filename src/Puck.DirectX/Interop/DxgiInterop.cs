using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;

namespace Puck.DirectX.Interop;

/// <summary>
/// Low-level DXGI helpers shared by the native APIs: creating a factory and packing adapter LUIDs.
/// </summary>
[SupportedOSPlatform("windows8.1")]
internal static unsafe class DxgiInterop {
    /// <summary>Creates an <c>IDXGIFactory4</c>; the caller owns the returned pointer and must <c>Release</c> it.</summary>
    /// <returns>A pointer to the newly created factory.</returns>
    /// <exception cref="DirectXException"><c>CreateDXGIFactory2</c> failed.</exception>
    public static IDXGIFactory4* CreateFactory() {
        void* factory;
        var result = PInvoke.CreateDXGIFactory2(
            Flags: default,
            riid: IDXGIFactory4.IID_Guid,
            ppFactory: out factory
        );

        result.ThrowIfFailed(operation: "CreateDXGIFactory2");
        return (IDXGIFactory4*)factory;
    }
    /// <summary>Packs a native <c>LUID</c> into a single 64-bit value.</summary>
    /// <param name="luid">The locally unique identifier to pack.</param>
    /// <returns>The identifier as <c>(HighPart &lt;&lt; 32) | LowPart</c>.</returns>
    public static long ToLuid(in LUID luid) {
        return (((long)luid.HighPart) << 32) | luid.LowPart;
    }
}
