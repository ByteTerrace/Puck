namespace Puck.Abstractions.Gpu;

/// <summary>
/// A backend-neutral two-dimensional image with the usages it was created for: its native image and image-view handles,
/// its extent, format and usages, owned for its lifetime. One type serves every use: a compute shader writes it as a
/// storage image, a render pass draws into it as a color or depth attachment (<see cref="IGpuFramebuffer"/>), and a
/// shader samples it, each only when <see cref="Usage"/> declares it.
/// </summary>
public interface IGpuImage : IDisposable {
    /// <summary>Gets the image format.</summary>
    GpuPixelFormat Format { get; }
    /// <summary>Gets the image height in pixels.</summary>
    uint Height { get; }
    /// <summary>Gets the native image handle: the resource barriers, copies and readbacks name.</summary>
    nint ImageHandle { get; }
    /// <summary>Gets the native image-view handle: the view a descriptor binds and a framebuffer attaches.</summary>
    nint ImageViewHandle { get; }
    /// <summary>Gets the usages the image was created for.</summary>
    GpuImageUsage Usage { get; }
    /// <summary>Gets the image width in pixels.</summary>
    uint Width { get; }
}
