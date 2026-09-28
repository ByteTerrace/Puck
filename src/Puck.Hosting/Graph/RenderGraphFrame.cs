using Puck.Abstractions.Sources;

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
/// <summary>What a source instance's producer declares for one frame: the cadence its image follows and the extent it
/// negotiated, read from its descriptor (<c>ImageSourceDescriptor</c>). A source renders at this extent, never at a
/// footprint's, and a source whose producer has negotiated no extent (either axis zero) or declares nothing this frame is
/// not rendered.</summary>
/// <param name="Instance">The source instance's name.</param>
/// <param name="Cadence">When the producer writes a new image: a <see cref="ImageRefresh.Static"/> source renders once, a
/// <see cref="ImageRefresh.Tick"/> source at most once per completed simulation tick, and a
/// <see cref="ImageRefresh.Rate"/> source at most <see cref="ImageSourceCadence.RateHz"/> times a second of presented
/// frames.</param>
/// <param name="Width">The negotiated width, in pixels, or zero while none is negotiated.</param>
/// <param name="Height">The negotiated height, in pixels, or zero while none is negotiated.</param>
public readonly record struct RenderGraphSourceState(string Instance, ImageSourceCadence Cadence, int Width, int Height);
/// <summary>Everything the scheduler reads about one presented frame: what is visible and how large it appears, the
/// simulation tick it presents, and what each source's producer declares. It is a value, so a host describing each frame
/// over the same root, footprint and source lists allocates nothing.</summary>
/// <param name="Index">The presented frame's index, non-negative and increasing from frame to frame.</param>
/// <param name="DisplayWidth">The display's width, in pixels.</param>
/// <param name="DisplayHeight">The display's height, in pixels.</param>
/// <param name="DisplayHertz">The display's presented frames a second, or zero when unknown.</param>
/// <param name="Roots">The instances the display shows directly.</param>
/// <param name="Footprints">The reads visible inside rendering instances.</param>
/// <param name="PassPixelBudget">The scheduling policy's price ceiling: the pass-pixels (passes times pixels) the
/// instances the display does not show directly may spend this frame, or zero for no ceiling. Roots always render.</param>
/// <param name="Tick">The completed simulation tick the frame presents, which paces a
/// <see cref="ImageRefresh.Tick"/> source: it renders again only on a frame whose tick differs from the one it last
/// rendered at.</param>
/// <param name="Sources">What each source instance's producer declares this frame, at most one entry per source, or
/// <see langword="null"/> for none. A source with no entry is not rendered.</param>
/// <param name="Rerender">The instances the frame renders again whatever their refresh and the pass-pixel budget,
/// whenever it demands them, or <see langword="null"/> for none: a capture frame's tainted instances, whose latest output
/// read external content the capture gate did not fill, so no capture reads an image a slower instance rendered from it.
/// A source is never named, since it renders at its producer's cadence and its consumers resolve its image as they
/// bind it.</param>
/// <param name="Unchanged">The instances whose host declares that nothing they render from has changed since their
/// latest completed render, so that render stands for this frame, or <see langword="null"/> for none: an SDF view whose
/// inputs are byte-identical to its last rendered frame's. Such an instance is not due by its refresh; it renders only
/// when it never has, when <paramref name="Rerender"/> names it, or when the extent it is demanded at moves, since a new
/// image holds nothing. A source is never named, since its producer declares its own cadence.</param>
public readonly record struct RenderGraphFrame(
    long Index,
    int DisplayWidth,
    int DisplayHeight,
    int DisplayHertz,
    IReadOnlyList<RenderGraphRoot> Roots,
    IReadOnlyList<RenderGraphFootprint> Footprints,
    long PassPixelBudget = 0,
    long Tick = 0,
    IReadOnlyList<RenderGraphSourceState>? Sources = null,
    IReadOnlyList<string>? Rerender = null,
    IReadOnlyList<string>? Unchanged = null
);
