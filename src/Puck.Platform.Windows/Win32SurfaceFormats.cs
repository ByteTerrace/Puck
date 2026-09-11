using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.Platform.Windows;

// The one SurfaceFormat -> DXGI_FORMAT mapping for this platform. Every Direct3D surface the platform opens for a
// neutral SurfaceFormat resolves it here, so adding a format is a single edit rather than a hunt for the copies.
internal static class Win32SurfaceFormats {
    public static DXGI_FORMAT ToDxgiFormat(SurfaceFormat format, string role) => (format switch {
        SurfaceFormat.R8G8B8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
        SurfaceFormat.B8G8R8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        _ => throw new NotSupportedException(message: $"{role} format {format} is unsupported"),
    });
}
