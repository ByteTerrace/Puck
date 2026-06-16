namespace Puck.Compositing;

public static class SplitLayouts {
    public const string DefaultName = "single";
    public const int ViewportCount = 4;

    public static IReadOnlyList<string> Ordered { get; } = ["single", "split", "quad", "pip"];

    public static SplitLayout? TryGet(string name) {
        ArgumentNullException.ThrowIfNull(name);

        return name.ToLowerInvariant() switch {
            "single" => Single(),
            "split" => SideBySide(),
            "quad" => Quad(),
            "pip" => PictureInPicture(),
            _ => null,
        };
    }

    private static SplitLayout Single() {
        return Build(
            curve: TransitionCurve.EaseInOutQuadratic,
            name: "single",
            regions: [(0, new NormalizedRect(
                Height: 1f,
                Width: 1f,
                X: 0f,
                Y: 0f
            ))]
        );
    }
    private static SplitLayout SideBySide() {
        return Build(
            curve: TransitionCurve.EaseInOutQuadratic,
            name: "split",
            regions: [
                (0, new NormalizedRect(
                    Height: 1f,
                    Width: 0.5f,
                    X: 0f,
                    Y: 0f
                )),
                (1, new NormalizedRect(
                    Height: 1f,
                    Width: 0.5f,
                    X: 0.5f,
                    Y: 0f
                )),
            ]
        );
    }
    private static SplitLayout Quad() {
        return Build(
            curve: TransitionCurve.Overshoot,
            name: "quad",
            regions: [
                (0, new NormalizedRect(
                    Height: 0.5f,
                    Width: 0.5f,
                    X: 0f,
                    Y: 0f
                )),
                (1, new NormalizedRect(
                    Height: 0.5f,
                    Width: 0.5f,
                    X: 0.5f,
                    Y: 0f
                )),
                (2, new NormalizedRect(
                    Height: 0.5f,
                    Width: 0.5f,
                    X: 0f,
                    Y: 0.5f
                )),
                (3, new NormalizedRect(
                    Height: 0.5f,
                    Width: 0.5f,
                    X: 0.5f,
                    Y: 0.5f
                )),
            ]
        );
    }
    private static SplitLayout PictureInPicture() {
        return Build(
            curve: TransitionCurve.Warp,
            name: "pip",
            regions: [
                (0, new NormalizedRect(
                    Height: 1f,
                    Width: 1f,
                    X: 0f,
                    Y: 0f
                )),
                (3, new NormalizedRect(
                    Height: 0.3f,
                    Width: 0.32f,
                    X: 0.64f,
                    Y: 0.66f
                )),
            ]
        );
    }
    private static SplitLayout Build(string name, TransitionCurve curve, (int Index, NormalizedRect Rect)[] regions) {
        var rects = new NormalizedRect[ViewportCount];

        Array.Fill(
            array: rects,
            value: NormalizedRect.Hidden
        );

        foreach (var (index, rect) in regions) {
            rects[index] = rect;
        }

        return new SplitLayout(
            curve: curve,
            name: name,
            regions: rects
        );
    }
}
