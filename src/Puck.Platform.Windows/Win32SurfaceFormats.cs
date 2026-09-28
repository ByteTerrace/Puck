using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.Platform.Windows;

// The one surface format -> DXGI_FORMAT mapping of this platform's Direct3D 11 surfaces (cameras, captures, probe rings).
// It names the surface formats (Surface.IsSurfaceFormat) and the float working format a view export's probe ring carries
// (RenderGraphPackageCatalog.WorkingFormat); Puck.DirectX's DirectXGpuFormats maps the whole vocabulary for the Direct3D
// 12 backend, which this project does not reference.
internal static class Win32SurfaceFormats {
    public static DXGI_FORMAT ToDxgiFormat(GpuPixelFormat format, string role) => (format switch {
        GpuPixelFormat.R8G8B8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
        GpuPixelFormat.B8G8R8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        GpuPixelFormat.R16G16B16A16Float => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
        _ => throw new NotSupportedException(message: $"{role} format {format} is unsupported"),
    });
}
