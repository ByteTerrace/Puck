namespace Puck.Compositing;

public readonly record struct NormalizedRect(float X, float Y, float Width, float Height) {
    public static NormalizedRect Hidden => new(
        Height: 0f,
        Width: 0f,
        X: 0.5f,
        Y: 0.5f
    );

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
