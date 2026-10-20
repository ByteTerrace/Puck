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
            Height: float.Lerp(
                value1: from.Height,
                value2: to.Height,
                amount: t
            ),
            Width: float.Lerp(
                value1: from.Width,
                value2: to.Width,
                amount: t
            ),
            X: float.Lerp(
                value1: from.X,
                value2: to.X,
                amount: t
            ),
            Y: float.Lerp(
                value1: from.Y,
                value2: to.Y,
                amount: t
            )
        );
    }
}
