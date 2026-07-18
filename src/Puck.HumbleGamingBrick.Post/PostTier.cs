namespace Puck.HumbleGamingBrick.Post;

/// <summary>The ordered fast→slow tier an <see cref="IPostStage"/> belongs to. The battery runs tiers A→C in order, and
/// the <c>--tier</c> option selects a single tier.</summary>
internal enum PostTier {
    /// <summary>Core self-tests — determinism, snapshot fidelity, throughput. Run on a self-contained synthetic ROM, so
    /// they need no external assets and run anywhere.</summary>
    A,
    /// <summary>Reference-ROM behavioural checks (conformance / acceptance ROMs / screenshot diff). Need the external test corpus;
    /// they skip (never fail) when it is absent.</summary>
    B,
    /// <summary>Cross-machine link determinism — two linked machines exchanging over the serial cable, deterministically
    /// and replay-identically (the serial-link stage; self-contained, runs anywhere). Grows toward the ideal plan's
    /// rule-3 cross-generation bit-lock once the GBA side has a link peer.</summary>
    C,
}
