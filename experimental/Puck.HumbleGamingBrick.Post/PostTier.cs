namespace Puck.HumbleGamingBrick.Post;

/// <summary>The ordered fast→slow tier an <see cref="IPostStage"/> belongs to. The battery runs tiers A→C in order, and
/// the <c>--tier</c> option selects a single tier.</summary>
internal enum PostTier {
    /// <summary>Core self-tests — determinism, snapshot fidelity, throughput. Run on a self-contained synthetic ROM, so
    /// they need no external assets and run anywhere.</summary>
    A,
    /// <summary>Reference-ROM behavioural checks (blargg / mooneye / screenshot diff). Need the external test corpus;
    /// they skip (never fail) when it is absent.</summary>
    B,
    /// <summary>Cross-generation determinism — two-machine link-lock and replay-through-churn. The rule-3 gate.</summary>
    C,
}
