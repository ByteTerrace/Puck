namespace Puck.Abstractions;

/// <summary>
/// A backend-neutral render target: an offscreen image with its associated framebuffer, render pass, and
/// command buffer. Each backend maps these handles to its native objects (a backend without a first-class
/// render-pass or framebuffer object reuses the image/attachment handles).
/// </summary>
public interface IGpuRenderTarget : IDisposable {
    /// <summary>Gets the native command buffer handle allocated for this target.</summary>
    nint CommandBufferHandle { get; }
    /// <summary>Gets the native framebuffer handle.</summary>
    nint FramebufferHandle { get; }
    /// <summary>Gets the height, in pixels, of the render target.</summary>
    uint Height { get; }
    /// <summary>Gets the native image handle.</summary>
    nint ImageHandle { get; }
    /// <summary>Gets the native image view handle.</summary>
    nint ImageViewHandle { get; }
    /// <summary>Gets the native render pass handle.</summary>
    nint RenderPassHandle { get; }
    /// <summary>Gets the width, in pixels, of the render target.</summary>
    uint Width { get; }
}
