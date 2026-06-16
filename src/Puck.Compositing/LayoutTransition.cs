namespace Puck.Compositing;

public sealed class LayoutTransition {
    private readonly float m_transitionSeconds;
    private SplitLayout m_fromLayout;
    private SplitLayout m_toLayout;
    private float m_progress = 1f;

    public LayoutTransition(SplitLayout initial, float transitionSeconds) {
        ArgumentNullException.ThrowIfNull(initial);

        m_fromLayout = initial;
        m_toLayout = initial;
        m_transitionSeconds = transitionSeconds;
    }

    public string TargetName => m_toLayout.Name;
    public float WarpAmount => (((m_toLayout.Curve == TransitionCurve.Warp) && (m_progress < 1f))
        ? MathF.Sin(x: (m_progress * MathF.PI))
        : 0f);

    public void Advance(float deltaSeconds) {
        if (m_progress < 1f) {
            m_progress = Math.Min(
                val1: 1f,
                val2: (m_progress + (deltaSeconds / m_transitionSeconds))
            );
        }
    }
    public void BeginTransition(SplitLayout target) {
        ArgumentNullException.ThrowIfNull(target);

        m_fromLayout = m_toLayout;
        m_toLayout = target;
        m_progress = 0f;
    }
    public NormalizedRect RegionFor(int viewportIndex) {
        var eased = TransitionCurves.Ease(
            curve: m_toLayout.Curve,
            progress: m_progress
        );

        return NormalizedRect.Lerp(
            from: m_fromLayout.RegionFor(viewportIndex: viewportIndex),
            t: eased,
            to: m_toLayout.RegionFor(viewportIndex: viewportIndex)
        );
    }
}
