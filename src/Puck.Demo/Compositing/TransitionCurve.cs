namespace Puck.Demo.Compositing;

/// <summary>How a split-screen layout transition eases from 0 to 1. <see cref="Warp"/> additionally drives
/// a shader-side ripple distortion while the transition is in flight.</summary>
internal enum TransitionCurve {
    /// <summary>Constant rate.</summary>
    Linear,

    /// <summary>Smooth acceleration then deceleration.</summary>
    EaseInOutQuadratic,

    /// <summary>Overshoots the target then settles back (a springy snap).</summary>
    Overshoot,

    /// <summary>Eased like <see cref="EaseInOutQuadratic"/>, plus a shader-driven ripple that peaks
    /// mid-transition.</summary>
    Warp,
}
