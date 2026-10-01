using Puck.Abstractions.Presentation;

namespace Puck.Hosting;

/// <summary>Whether a render root rendered the frame a host asked it to compose.</summary>
public enum FrameCompletion {
    /// <summary>The root's image shows the frame it was asked to compose: rendered from that frame's state over inputs
    /// that are current for it, or standing unchanged because nothing it shows moved.</summary>
    Rendered,
    /// <summary>The root could not render the frame yet, and composing the same frame again may: a pipeline still
    /// building, a graph rebuilding, or an input with no output for the frame. Any image it returns is an older
    /// frame's.</summary>
    NotYetRenderable,
    /// <summary>The root cannot render the frame until something it was built from changes: a build it depends on was
    /// refused by name. Any image it returns is an older frame's.</summary>
    Refused,
}
/// <summary>What a render root produced for one frame: the surface to present and whether that surface shows the frame
/// it was asked to compose.</summary>
/// <param name="Surface">The surface the host presents, valid until the root's next frame; empty when the root has
/// nothing to present.</param>
/// <param name="Completion">Whether the surface shows the frame the host asked for.</param>
/// <param name="Reason">Why the frame is not rendered, naming the instance and its state; <see langword="null"/> when it
/// is.</param>
public readonly record struct RootFrame(Surface Surface, FrameCompletion Completion, string? Reason = null) {
    /// <summary>Returns a frame whose surface shows the frame it was asked to compose.</summary>
    /// <param name="surface">The rendered surface.</param>
    /// <returns>The rendered frame.</returns>
    public static RootFrame Rendered(Surface surface) => new(
        Completion: FrameCompletion.Rendered,
        Surface: surface
    );
}
