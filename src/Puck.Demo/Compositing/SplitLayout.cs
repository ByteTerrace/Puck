namespace Puck.Demo.Compositing;

/// <summary>A named split-screen layout: the screen region each viewport occupies (indexed by viewport
/// ordinal), plus the curve used when transitioning to it. Viewports absent from the layout get
/// <see cref="NormalizedRect.Hidden"/>, so they scale out of the composition.</summary>
internal sealed class SplitLayout {
    private readonly NormalizedRect[] m_regions;

    public SplitLayout(string name, TransitionCurve curve, NormalizedRect[] regions) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(regions);

        Curve = curve;
        Name = name;
        m_regions = regions;
    }

    /// <summary>The curve used when transitioning to this layout.</summary>
    public TransitionCurve Curve { get; }

    /// <summary>The layout's name (the <c>layout</c> command argument).</summary>
    public string Name { get; }

    /// <summary>The number of viewports the layout addresses.</summary>
    public int ViewportCount => m_regions.Length;

    /// <summary>The screen region for a viewport, or <see cref="NormalizedRect.Hidden"/> if absent.</summary>
    public NormalizedRect RegionFor(int viewportIndex) {
        return m_regions[viewportIndex];
    }
}
