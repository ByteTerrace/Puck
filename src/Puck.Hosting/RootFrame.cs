using Puck.Abstractions.Presentation;

namespace Puck.Hosting;

/// <summary>Whether a frame, or an image a producer was asked for, was rendered.</summary>
public enum FrameCompletion {
    /// <summary>The image shows the frame it was asked for: rendered from that frame's state over inputs that are current
    /// for it, or standing unchanged because nothing it shows moved.</summary>
    Rendered,
    /// <summary>The image could not be rendered yet, and asking again may render it: a pipeline still building, a graph
    /// rebuilding, an input with no output for the frame, a source with no image yet. Any image handed out is an older
    /// frame's.</summary>
    NotYetRenderable,
    /// <summary>The image cannot be rendered until something it was built from changes: a build refused by name, a feed
    /// that ended. Any image handed out is an older frame's.</summary>
    Refused,
}
/// <summary>Whether a frame or a producer's image was rendered, and why not when it was not: the one three-way answer a
/// render root, the render graph runtime and every producer it runs give.</summary>
/// <param name="Completion">Whether it was rendered.</param>
/// <param name="Reason">Why it was not, naming what waits or refused; <see langword="null"/> when it was.</param>
public readonly record struct FrameRender(FrameCompletion Completion, string? Reason = null) {
    /// <summary>Gets the answer of an image that was rendered.</summary>
    public static FrameRender Rendered { get; } = new(Completion: FrameCompletion.Rendered);
    /// <summary>Gets whether the image was rendered.</summary>
    public bool IsRendered => (Completion == FrameCompletion.Rendered);

    /// <summary>Returns the answer of an image that cannot be rendered yet.</summary>
    /// <param name="reason">What it waits for.</param>
    /// <returns>The answer.</returns>
    public static FrameRender Waiting(string reason) => new(
        Completion: FrameCompletion.NotYetRenderable,
        Reason: reason
    );
    /// <summary>Returns the answer of an image that cannot be rendered until something it was built from changes.</summary>
    /// <param name="reason">The refusal, naming what refused.</param>
    /// <returns>The answer.</returns>
    public static FrameRender Refused(string reason) => new(
        Completion: FrameCompletion.Refused,
        Reason: reason
    );
}
/// <summary>What a render root produced for one frame: the surface to present and whether that surface shows the frame
/// it was asked to compose.</summary>
/// <param name="Surface">The surface the host presents, valid until the root's next frame; empty when the root has
/// nothing to present.</param>
/// <param name="Render">Whether the surface shows the frame the host asked for, and why not.</param>
public readonly record struct RootFrame(Surface Surface, FrameRender Render);
