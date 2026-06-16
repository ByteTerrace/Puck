namespace Puck.Compositing;

public sealed class SplitLayout {
    private readonly NormalizedRect[] m_regions;

    public SplitLayout(string name, TransitionCurve curve, NormalizedRect[] regions) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(regions);

        Curve = curve;
        Name = name;
        m_regions = regions;
    }

    public TransitionCurve Curve { get; }
    public string Name { get; }
    public int ViewportCount => m_regions.Length;

    public NormalizedRect RegionFor(int viewportIndex) {
        return m_regions[viewportIndex];
    }
}
