namespace Puck.Platform.Shared;

public readonly record struct TerminalViewport(float X, float Y, float Width, float Height) {
    public static TerminalViewport Fullscreen { get; } = new(
        Height: 1f,
        Width: 1f,
        X: 0f,
        Y: 0f
    );

    public static IReadOnlyList<TerminalViewport> CreateSplitScreenLayout(int participantCount) {
        return participantCount switch {
            <= 0 => throw new ArgumentOutOfRangeException(
                message: "Participant count must be greater than zero.",
                paramName: nameof(participantCount)
            ),
            1 => [Fullscreen],
            2 =>
            [
                new TerminalViewport(
                    Height: 1f,
                    Width: 0.5f,
                    X: 0f,
                    Y: 0f
                ),
                new TerminalViewport(
                    Height: 1f,
                    Width: 0.5f,
                    X: 0.5f,
                    Y: 0f
                )
            ],
            3 =>
            [
                new TerminalViewport(
                    Height: 0.5f,
                    Width: 0.5f,
                    X: 0f,
                    Y: 0f
                ),
                new TerminalViewport(
                    Height: 0.5f,
                    Width: 0.5f,
                    X: 0.5f,
                    Y: 0f
                ),
                new TerminalViewport(
                    Height: 0.5f,
                    Width: 1f,
                    X: 0f,
                    Y: 0.5f
                )
            ],
            4 =>
            [
                new TerminalViewport(
                    Height: 0.5f,
                    Width: 0.5f,
                    X: 0f,
                    Y: 0f
                ),
                new TerminalViewport(
                    Height: 0.5f,
                    Width: 0.5f,
                    X: 0.5f,
                    Y: 0f
                ),
                new TerminalViewport(
                    Height: 0.5f,
                    Width: 0.5f,
                    X: 0f,
                    Y: 0.5f
                ),
                new TerminalViewport(
                    Height: 0.5f,
                    Width: 0.5f,
                    X: 0.5f,
                    Y: 0.5f
                )
            ],
            _ => throw new ArgumentOutOfRangeException(
                message: "Split-screen layout currently supports up to four participants.",
                paramName: nameof(participantCount)
            )
        };
    }
    public static TerminalViewport Interpolate(TerminalViewport from, TerminalViewport to, float progress) {
        return new TerminalViewport(
            Height: Lerp(
                from: from.Height,
                progress: progress,
                to: to.Height
            ),
            Width: Lerp(
                from: from.Width,
                progress: progress,
                to: to.Width
            ),
            X: Lerp(
                from: from.X,
                progress: progress,
                to: to.X
            ),
            Y: Lerp(
                from: from.Y,
                progress: progress,
                to: to.Y
            )
        );
    }

    private static float Lerp(float from, float to, float progress) {
        return (from + ((to - from) * progress));
    }

    public bool IsValid => ((X >= 0f)
        && (Y >= 0f)
        && (Width > 0f)
        && (Height > 0f)
        && ((X + Width) <= 1f)
        && ((Y + Height) <= 1f));
}
