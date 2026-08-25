using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Hosting;

// The surface-readback orchestration both backend presenters need for a one-shot CPU-pixel capture: import a
// shared-handle surface onto the calling device when it did not already produce it, then read it back. Only the
// device-context type differs per backend; the format mapping is the shared GpuPixelFormats.FromSurfaceFormat bridge.
public static class SurfaceReadbackCapture {
    /// <summary>Reads a surface back to CPU pixels, importing it onto <paramref name="deviceContext"/> first when it
    /// carries a shared handle produced elsewhere. The caller handles empty and CPU-resident surfaces before
    /// entering this GPU path.</summary>
    /// <param name="surface">The surface to read back.</param>
    /// <param name="deviceContext">The calling backend's device context.</param>
    /// <param name="surfaceTransferFactory">Creates the import/readback objects, lazily cached by the caller.</param>
    /// <param name="toGpuFormat">Maps the surface's format to the backend's own <see cref="GpuPixelFormat"/>.</param>
    /// <param name="captureImport">The caller's cached import object; created on first shared-handle surface.</param>
    /// <param name="captureReadback">The caller's cached readback object; created on first call.</param>
    /// <returns>A CPU-pixel surface with the same format, width, and height.</returns>
    public static Surface ReadSurface(Surface surface, IGpuDeviceContext deviceContext, IGpuSurfaceTransferFactory surfaceTransferFactory, Func<SurfaceFormat, GpuPixelFormat> toGpuFormat, ref IGpuSurfaceImport? captureImport, ref IGpuSurfaceReadback? captureReadback) {
        var format = toGpuFormat(surface.Format);
        var imageHandle = surface.ImageHandle;

        if (surface.IsSharedHandle) {
            captureImport ??= surfaceTransferFactory.CreateImport(deviceContext: deviceContext);
            imageHandle = captureImport.Import(
                deviceContext: deviceContext,
                format: format,
                height: surface.Height,
                sharedHandle: surface.SharedHandle,
                width: surface.Width
            ).ImageHandle;
        }

        captureReadback ??= surfaceTransferFactory.CreateReadback(deviceContext: deviceContext);
        var pixels = captureReadback.Read(
            bytesPerPixel: 4,
            deviceContext: deviceContext,
            format: format,
            height: surface.Height,
            sourceImageHandle: imageHandle,
            sourceLayout: GpuImageLayout.ShaderReadOnly,
            width: surface.Width
        );

        return Surface.CpuPixels(
            format: surface.Format,
            height: surface.Height,
            pixels: pixels,
            width: surface.Width
        );
    }
}
