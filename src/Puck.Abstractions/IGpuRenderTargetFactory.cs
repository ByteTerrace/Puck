namespace Puck.Abstractions;

/// <summary>
/// Creates backend-neutral render targets for offscreen rendering.
/// </summary>
public interface IGpuRenderTargetFactory {
    /// <summary>Creates a render target of the given format and dimensions on the given device.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="format">The pixel format of the render target (a <see cref="GpuPixelFormat"/> constant).</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <returns>A new, owning <see cref="IGpuRenderTarget"/>.</returns>
    IGpuRenderTarget Create(IGpuDeviceContext deviceContext, uint format, uint width, uint height);
}
