using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX;

internal static class DirectXGpuFormats {
    internal static DXGI_FORMAT ToDxgiFormat(uint gpuPixelFormat) => gpuPixelFormat switch {
        GpuPixelFormat.R8G8B8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
        GpuPixelFormat.B8G8R8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: gpuPixelFormat,
            message: null,
            paramName: nameof(gpuPixelFormat)
        ),
    };
    internal static DirectXPixelFormat ToDirectXPixelFormat(uint gpuPixelFormat) => gpuPixelFormat switch {
        GpuPixelFormat.R8G8B8A8Unorm => DirectXPixelFormat.R8G8B8A8Unorm,
        GpuPixelFormat.B8G8R8A8Unorm => DirectXPixelFormat.B8G8R8A8Unorm,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: gpuPixelFormat,
            message: null,
            paramName: nameof(gpuPixelFormat)
        ),
    };
}
