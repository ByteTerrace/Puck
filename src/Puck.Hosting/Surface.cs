namespace Puck.Hosting;

/// <summary>
/// The rendered pixels a node hands its host to composite, in one of two variants.
/// <para>
/// The <em>GPU</em> variant carries a shared image-view handle (<see cref="ImageViewHandle"/>) — valid only
/// while producer and consumer share one device chain, already in a shader-readable layout — so the consumer
/// may sample it immediately.
/// </para>
/// <para>
/// The <em>CPU-pixel</em> variant carries a tightly packed readback buffer (<see cref="Pixels"/>) instead of a
/// device handle. It is the serialize-/remote-able payload that lets a surface cross a device (or process)
/// boundary: a producer on one backend reads its result back to host memory, and the consumer uploads those
/// pixels onto its own device before sampling. Rows are packed with no padding, in <see cref="Format"/> order.
/// </para>
/// In both variants the producer has serialized its work before returning.
/// </summary>
/// <param name="ImageViewHandle">The shared <c>VkImageView</c> handle of the same-device GPU variant, or zero otherwise.</param>
/// <param name="Width">The width, in pixels, of the surface.</param>
/// <param name="Height">The height, in pixels, of the surface.</param>
/// <param name="Format">The backend-neutral pixel format the texels are laid out in.</param>
/// <param name="Pixels">The tightly packed pixels of the CPU-pixel variant, or empty otherwise.</param>
/// <param name="SharedHandle">The shared external (NT) handle of the zero-copy cross-device variant — a texture another backend produced in shared GPU memory — or zero otherwise.</param>
public readonly record struct Surface(
    nint ImageViewHandle,
    uint Width,
    uint Height,
    SurfaceFormat Format,
    ReadOnlyMemory<byte> Pixels = default,
    nint SharedHandle = 0
) {
    /// <summary>Gets whether the surface carries no content (no GPU handle, CPU pixels, or shared handle).</summary>
    public bool IsEmpty =>
        ((0 == ImageViewHandle) && Pixels.IsEmpty && (0 == SharedHandle));
    /// <summary>Gets whether the surface is the CPU-pixel variant — a host-memory readback rather than a device handle.</summary>
    public bool IsCpuPixels =>
        !Pixels.IsEmpty;
    /// <summary>Gets whether the surface is the zero-copy shared-handle variant — an external texture in shared GPU memory.</summary>
    public bool IsSharedHandle =>
        (0 != SharedHandle);
}
