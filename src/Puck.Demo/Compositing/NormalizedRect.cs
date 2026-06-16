namespace Puck.Demo.Compositing;

/// <summary>A normalized screen rectangle (origin top-left, (0,0)..(1,1)). A viewport's region on the
/// screen; a zero-size rect (see <see cref="Hidden"/>) means the viewport is not shown.</summary>
internal readonly record struct NormalizedRect(float X, float Y, float Width, float Height) {
    /// <summary>A collapsed rect at screen center, used for viewports absent from a layout so they scale
    /// in/out from the middle during a transition.</summary>
    public static NormalizedRect Hidden => new(
        Height: 0f,
        Width: 0f,
        X: 0.5f,
        Y: 0.5f
    );

    /// <summary>Component-wise linear interpolation between two rects.</summary>
    public static NormalizedRect Lerp(NormalizedRect from, NormalizedRect to, float t) {
        return new NormalizedRect(
            Height: Interpolate(
                from: from.Height,
                to: to.Height,
                t: t
            ),
            Width: Interpolate(
                from: from.Width,
                to: to.Width,
                t: t
            ),
            X: Interpolate(
                from: from.X,
                to: to.X,
                t: t
            ),
            Y: Interpolate(
                from: from.Y,
                to: to.Y,
                t: t
            )
        );
    }

    private static float Interpolate(float from, float to, float t) {
        return (from + ((to - from) * t));
    }
}
