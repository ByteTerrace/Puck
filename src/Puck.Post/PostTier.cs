namespace Puck.Post;

/// <summary>The ordered fast→slow tier an <see cref="IPostStage"/> belongs to. The battery runs tiers A→D in order, and
/// the <c>--tier</c> option selects a single tier.</summary>
internal enum PostTier {
    /// <summary>Pure-CPU pre-flight (no GPU); runs anywhere.</summary>
    A,
    /// <summary>Same-device GPU smoke on the offscreen Vulkan host.</summary>
    B,
    /// <summary>Cross-backend: the Vulkan host plus a LUID-matched Direct3D 12 device.</summary>
    C,
    /// <summary>Live-only subsystems (device-loss recovery, backend hot-switch).</summary>
    D,
}
