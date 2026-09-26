using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.Platform.Windows;

// The one surface format -> DXGI_FORMAT mapping of this platform's Direct3D 11 surfaces (cameras, captures, probe rings).
// It names only the surface formats (Surface.IsSurfaceFormat); Puck.DirectX's DirectXGpuFormats maps the whole
// vocabulary for the Direct3D 12 backend, which this project does not reference.
internal static class Win32SurfaceFormats {
    public static DXGI_FORMAT ToDxgiFormat(GpuPixelFormat format, string role) => (format switch {
        GpuPixelFormat.R8G8B8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
        GpuPixelFormat.B8G8R8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        _ => throw new NotSupportedException(message: $"{role} format {format} is unsupported"),
    });
}
