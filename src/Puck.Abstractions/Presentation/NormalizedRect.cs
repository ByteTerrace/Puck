namespace Puck.Abstractions.Presentation;

/// <summary>A rectangle expressed in normalized presentation coordinates. Values are not clamped, allowing transitions to move off-screen.</summary>
/// <param name="X">The normalized horizontal origin.</param>
/// <param name="Y">The normalized vertical origin.</param>
/// <param name="Width">The normalized width.</param>
/// <param name="Height">The normalized height.</param>
public readonly record struct NormalizedRect(float X, float Y, float Width, float Height) {
    /// <summary>Gets the zero-area centered rectangle used to hide a presentation element.</summary>
    public static NormalizedRect Hidden => new(
        Height: 0f,
        Width: 0f,
        X: 0.5f,
        Y: 0.5f
    );

    /// <summary>Linearly interpolates every rectangle component. <paramref name="t"/> is not clamped.</summary>
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
