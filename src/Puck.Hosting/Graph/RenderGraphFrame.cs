namespace Puck.Hosting;

/// <summary>An instance the display shows directly, such as the main camera or a pane.</summary>
/// <param name="Instance">The instance name.</param>
/// <param name="Width">The fraction of the display's width it covers.</param>
/// <param name="Height">The fraction of the display's height it covers.</param>
public readonly record struct RenderGraphRoot(string Instance, double Width, double Height);
/// <summary>How much of one rendering instance's image another instance's output covers this frame, such as a screen
/// showing a game camera. A footprint of zero on either axis, or no footprint at all, means the consumer does not show
/// the producer this frame.</summary>
/// <param name="Consumer">The instance that shows the producer.</param>
/// <param name="Producer">The instance shown; the consumer must declare a read of it.</param>
/// <param name="Width">The fraction of the consumer's width the producer covers.</param>
/// <param name="Height">The fraction of the consumer's height the producer covers.</param>
public readonly record struct RenderGraphFootprint(string Consumer, string Producer, double Width, double Height);
/// <summary>Everything the scheduler reads about one presented frame: what is visible and how large it appears. It is a
/// value, so a host describing each frame over the same root and footprint lists allocates nothing.</summary>
/// <param name="Index">The presented frame's index, non-negative and increasing from frame to frame.</param>
/// <param name="DisplayWidth">The display's width, in pixels.</param>
/// <param name="DisplayHeight">The display's height, in pixels.</param>
/// <param name="DisplayHertz">The display's presented frames a second, or zero when unknown.</param>
/// <param name="Roots">The instances the display shows directly.</param>
/// <param name="Footprints">The reads visible inside rendering instances.</param>
/// <param name="PassPixelBudget">The scheduling policy's price ceiling: the pass-pixels (passes times pixels) the
/// instances the display does not show directly may spend this frame, or zero for no ceiling. Roots always render.</param>
public readonly record struct RenderGraphFrame(
    long Index,
    int DisplayWidth,
    int DisplayHeight,
    int DisplayHertz,
    IReadOnlyList<RenderGraphRoot> Roots,
    IReadOnlyList<RenderGraphFootprint> Footprints,
    long PassPixelBudget = 0
);
