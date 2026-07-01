namespace Puck.Abstractions;

/// <summary>
/// The rendered pixels a node hands its host to composite, in one of three variants. At most one of
/// <see cref="ImageViewHandle"/>, <see cref="Pixels"/>, and <see cref="SharedHandle"/> is populated — the
/// consumer discriminates via <see cref="IsCpuPixels"/> and <see cref="IsSharedHandle"/> (else same-device GPU),
/// with <see cref="IsEmpty"/> meaning "no content this frame".
/// <para>
/// The <em>same-device GPU</em> variant carries an image-view handle (<see cref="ImageViewHandle"/>) — valid only
/// while producer and consumer share one device chain, already in a shader-readable layout — so the consumer
/// may sample it immediately.
/// </para>
/// <para>
/// The <em>CPU-pixel</em> variant carries a tightly packed readback buffer (<see cref="Pixels"/>) instead of a
/// device handle. It is the serialize-/remote-able payload that lets a surface cross a device (or process)
/// boundary: a producer on one backend reads its result back to host memory, and the consumer uploads those
/// pixels onto its own device before sampling. Rows are packed with no padding, in <see cref="Format"/> order.
/// </para>
/// <para>
/// The <em>shared-handle</em> variant carries an external shareable handle (<see cref="SharedHandle"/>) to a
/// texture the producer allocated in shared GPU memory, so a consumer on a DIFFERENT backend/device can import
/// and sample it zero-copy — no host-memory round trip.
/// </para>
/// In all variants the producer has serialized its work before returning.
/// </summary>
/// <param name="ImageViewHandle">The native image-view handle of the same-device GPU variant, or zero otherwise.</param>
/// <param name="Width">The width, in pixels, of the surface.</param>
/// <param name="Height">The height, in pixels, of the surface.</param>
/// <param name="Format">The backend-neutral pixel format the texels are laid out in.</param>
/// <param name="Pixels">The tightly packed pixels of the CPU-pixel variant, or empty otherwise.</param>
/// <param name="SharedHandle">The shared external handle (a Windows NT handle, or a POSIX file descriptor on other platforms) of the zero-copy cross-device variant — a texture another backend produced in shared GPU memory — or zero otherwise.</param>
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
