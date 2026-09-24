using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX.Interop;

/// <summary>
/// The device context, DXGI format, and extent a Direct3D 12 image is allocated with. Every image factory builds one
/// with <see cref="From"/>, so the neutral-to-Direct3D 12 translation (the device-context cast and the
/// <see cref="GpuPixelFormat"/> to <c>DXGI_FORMAT</c> conversion) happens in one place.
/// </summary>
/// <param name="DeviceContext">The Direct3D 12 device context that allocates the image.</param>
/// <param name="Format">The image's DXGI format.</param>
/// <param name="Width">The image width in pixels.</param>
/// <param name="Height">The image height in pixels.</param>
[SupportedOSPlatform("windows10.0.10240")]
public readonly record struct DirectXGpuImageRequest(IDirectXDeviceContext DeviceContext, DXGI_FORMAT Format, uint Width, uint Height) {
    /// <summary>Translates neutral image-factory arguments into a Direct3D 12 image request.</summary>
    /// <param name="deviceContext">The device context, which must be a Direct3D 12 context.</param>
    /// <param name="format">The neutral pixel format.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>The request carrying the Direct3D 12 context and DXGI format.</returns>
    /// <exception cref="InvalidCastException"><paramref name="deviceContext"/> is not a Direct3D 12 device context.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> has no DXGI equivalent.</exception>
    public static DirectXGpuImageRequest From(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height) =>
        new(
            DeviceContext: ((IDirectXDeviceContext)deviceContext),
            Format: DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format),
            Height: height,
            Width: width
        );
}
