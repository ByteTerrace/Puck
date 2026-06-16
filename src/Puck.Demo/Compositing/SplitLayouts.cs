namespace Puck.Demo.Compositing;

/// <summary>The demo's split-screen layout presets, over a fixed pool of <see cref="ViewportCount"/>
/// viewports. Each places a subset of the viewports into screen regions; switching between them animates
/// (the <c>layout</c> command, or the left/right arrows cycle through <see cref="Ordered"/>).</summary>
internal static class SplitLayouts {
    /// <summary>The number of viewports the demo renders (and the layouts address).</summary>
    public const int ViewportCount = 4;

    /// <summary>The default layout's name.</summary>
    public const string DefaultName = "single";

    /// <summary>The layout names in cycle order (left/right arrows step through these).</summary>
    public static IReadOnlyList<string> Ordered { get; } = ["single", "split", "quad", "pip"];

    /// <summary>Builds the named layout, or <see langword="null"/> if the name is unknown.</summary>
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

    // Viewport 0 fills the screen.
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
    // Viewports 0 and 1 share the screen left/right.
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
    // All four viewports in quadrants (a springy snap).
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
    // Viewport 0 full-screen with viewport 3 inset bottom-right (a shader-warped transition).
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
