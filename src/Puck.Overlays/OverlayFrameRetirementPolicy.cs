namespace Puck.Overlays;

/// <summary>The ways <see cref="UnifiedOverlayNode"/> can end a produced frame with no fresh overlay submission of
/// its own to prove retirement, plus device loss — the exits <see cref="OverlayFrameRetirementPolicy"/> maps to a
/// retirement.</summary>
public enum OverlayFrameExit {
    /// <summary>The inner producer's frame was empty or unbound — there is nothing for the overlay pass to sample.</summary>
    NoInnerFrame,
    /// <summary>The inner frame was live, but no writer emitted any content this produced frame.</summary>
    NoOverlayContent,
    /// <summary>The device was lost.</summary>
    DeviceLost,
}

/// <summary>Which <see cref="OverlayFrameSlots"/> retirement one <see cref="OverlayFrameExit"/> takes — pulled out
/// of <see cref="UnifiedOverlayNode"/> so its exit points read one shared table instead of each repeating the
/// decision.</summary>
public static class OverlayFrameRetirementPolicy {
    /// <summary>Gets a value indicating whether <paramref name="exit"/> must retire every held lease immediately
    /// (device loss — the last submitted pass's fence describes a device that no longer exists) rather than
    /// deferring to that fence to prove the last submitted pass retired.</summary>
    /// <param name="exit">The exit reason.</param>
    public static bool RetiresImmediately(OverlayFrameExit exit) => (OverlayFrameExit.DeviceLost == exit);
}
