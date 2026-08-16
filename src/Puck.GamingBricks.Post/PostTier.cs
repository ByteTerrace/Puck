namespace Puck.GamingBricks.Post;

/// <summary>The ordered fast→slow tier a battery stage belongs to. A battery runs tiers A→C in order, and the
/// <c>--tier</c> option selects a single tier.</summary>
public enum PostTier {
    /// <summary>Core self-tests, self-contained (no external assets) so they run anywhere.</summary>
    A,
    /// <summary>Reference-corpus behavioural checks. Need an external asset; skip (never fail) when it is absent.</summary>
    B,
    /// <summary>Cross-machine link or commercial-scenario determinism.</summary>
    C,
}
