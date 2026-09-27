using Puck.Commands;

namespace Puck.World;

/// <summary>Publishes a <see cref="WorldScreen"/> row as the <see cref="SourceMapping"/> a hit on its face maps through:
/// the row's face frame as a surface placement, the whole source stretched upright, and the screen glass's bezel as a
/// warp pass whose inverse is exact. Built from document data alone, so a <c>Simulation</c> screen maps a pointer ray to
/// the same source pixel on every run, and the screen shading draws the row from the same mapping.</summary>
public static class WorldScreenMappings {
    /// <summary>The name of the screen glass pass: the SDF view pass's screen shading, which draws every screen from its
    /// mapping.</summary>
    public const string GlassPass = "sdf.screen-glass";

    /// <summary>Gets the screen glass's warp: an inset that shows the whole image inside a bezel three hundredths of the
    /// face wide on every side. It is the one statement of the bezel: the screen shading draws it from each screen's
    /// mapping (<see cref="SourceMapping.Draw"/>), and a hit maps through its inverse.</summary>
    public static SourceWarp Glass { get; } = new(
        Inverse: SourceWarpInverse.Affine.Inset(border: 0.03f),
        Pass: GlassPass
    );

    /// <summary>Creates the mapping for a screen row showing a source.</summary>
    /// <param name="screen">The screen row.</param>
    /// <param name="source">The source the row shows.</param>
    /// <param name="sourceWidth">The source's width, in pixels.</param>
    /// <param name="sourceHeight">The source's height, in pixels.</param>
    /// <returns>The mapping, whose destination is the row's <see cref="WorldScreenRoute.Input"/>, whose filter is the
    /// row's <see cref="WorldScreen.Filter"/> and whose opener is <see cref="SourceOpener.Document"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="screen"/> is <see langword="null"/>.</exception>
    public static SourceMapping Of(WorldScreen screen, SourceHandle source, int sourceWidth, int sourceHeight) {
        ArgumentNullException.ThrowIfNull(argument: screen);

        return new SourceMapping(
            Crop: SourcePixelRect.Whole(
                height: sourceHeight,
                width: sourceWidth
            ),
            Destination: (screen.Route.Input ?? SourceDestination.Presentation),
            Filter: screen.Filter,
            Opener: SourceOpener.Document,
            Placement: new SourcePlacement.Surface(
                HalfHeight: screen.HalfHeight,
                HalfWidth: screen.HalfWidth,
                Origin: screen.Origin,
                Right: screen.Right,
                Up: screen.Up
            ),
            Source: source,
            SourceHeight: sourceHeight,
            SourceWidth: sourceWidth,
            Warp: Glass
        );
    }
    /// <summary>Creates the mapping the simulation maps a seat's pointer ray through: the row's mapping against a
    /// one-by-one source, so a hit's <see cref="SourceHit.Coordinate"/> is the source-normalized fraction of the image,
    /// in <c>[0, 1)</c> on the source. The row holds no source extent, so a rule that wants pixels multiplies by a
    /// resolution it knows.</summary>
    /// <param name="screen">The screen row.</param>
    /// <returns>The mapping.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="screen"/> is <see langword="null"/>.</exception>
    public static SourceMapping Normalized(WorldScreen screen) {
        ArgumentNullException.ThrowIfNull(argument: screen);

        return Of(
            screen: screen,
            source: SourceHandle.Producer(name: $"screens[{screen.Index}]"),
            sourceHeight: 1,
            sourceWidth: 1
        );
    }
}
