namespace Puck.Abstractions.Presentation;

/// <summary>
/// An optional <see cref="ISurfacePresenter"/> capability that converts the exact <see cref="Surface"/> supplied
/// by the host to tightly packed CPU pixels. The source is explicit: implementations never infer or retain a
/// "last presented" frame, so callers can associate the pixels with the same frame context that produced it.
/// </summary>
public interface IPresentSurfaceReadback {
    /// <summary>Reads <paramref name="surface"/> synchronously. CPU-pixel surfaces may be returned unchanged; an empty
    /// surface produces an empty result. The returned CPU memory is guaranteed only until the next call on this
    /// presenter, so a sink that retains it must copy it during consumption.</summary>
    /// <param name="surface">The current root surface, normally the same value subsequently handed to
    /// <see cref="ISurfacePresenter.Present"/>.</param>
    /// <returns>The same pixels as a CPU-pixel surface, or an empty surface when <paramref name="surface"/> is empty.</returns>
    Surface ReadSurface(Surface surface);
}
