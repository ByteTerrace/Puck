namespace Puck.AdvancedGamingBrick.Post;

/// <summary>The ordered fast&#8594;slow tier an <see cref="IPostStage"/> belongs to. The battery runs tiers A&#8594;C in
/// order, and the <c>--tier</c> option selects a single tier.</summary>
internal enum PostTier {
    /// <summary>Core self-tests — CPU/PPU/APU/IRQ smoke vectors, determinism, save-persistence round-trip, throughput.
    /// Run on hand-assembled vectors and a self-contained synthetic cartridge, so they need no external assets and run
    /// anywhere.</summary>
    A,
    /// <summary>Reference-ROM behavioural checks (the conformance corpus, the ARM fuzz corpus, the accuracy suite, the
    /// AGS aging cartridge, deterministic render-hash floors — see the README asset inventory). Need the external test
    /// corpus and/or a real replacement BIOS; they skip (never fail) when those are absent.</summary>
    B,
    /// <summary>Cross-machine link determinism — a two-console SIO multiplayer exchange over the real
    /// <see cref="AgbLinkCable"/>, stepped through <see cref="AgbLinkSession"/>'s deterministic furthest-behind
    /// interleave, then re-run from fresh machines and required to reproduce byte-identical final snapshots
    /// (<c>link-replay</c>), plus a mid-exchange suspend/snapshot/restore/reconnect churn through the session's
    /// credit-preserving resume token (<c>link-churn</c>). Self-contained (hand-assembled micro-ROMs, no BIOS
    /// dependence), so it runs anywhere.</summary>
    C,
}
