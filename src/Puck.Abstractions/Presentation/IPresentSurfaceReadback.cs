namespace Puck.Abstractions.Presentation;

/// <summary>
/// An optional <see cref="ISurfacePresenter"/> capability that converts the exact <see cref="Surface"/> supplied
/// by the host to tightly packed CPU pixels. The source is explicit: implementations never infer or retain a
/// "last presented" frame, so callers can associate the pixels with the same frame context that produced it.
/// </summary>
public interface IPresentSurfaceReadback {
    /// <summary>Reads <paramref name="surface"/> synchronously. An 8-bit CPU-pixel surface may be returned unchanged;
    /// an empty surface produces an empty result; CPU pixels or a same-device image in a float working format read
    /// back through the display encode's SDR, in <see cref="Gpu.GpuPixelFormat.R8G8B8A8Unorm"/>. The returned CPU memory
    /// is guaranteed only until the next call on this presenter, so a sink that retains it must copy it during
    /// consumption.</summary>
    /// <param name="surface">The current root surface, normally the same value subsequently handed to
    /// <see cref="ISurfacePresenter.Present"/>.</param>
    /// <returns>The same pixels as a CPU-pixel surface, in the source's format for an 8-bit surface and RGBA8 for a float
    /// working surface, or an empty surface when <paramref name="surface"/> is empty.</returns>
    Surface ReadSurface(Surface surface);
}
