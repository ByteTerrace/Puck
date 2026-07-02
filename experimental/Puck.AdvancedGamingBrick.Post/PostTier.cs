namespace Puck.AdvancedGamingBrick.Post;

/// <summary>The ordered fast&#8594;slow tier an <see cref="IPostStage"/> belongs to. The battery runs tiers A&#8594;C in
/// order, and the <c>--tier</c> option selects a single tier.</summary>
internal enum PostTier {
    /// <summary>Core self-tests — CPU/PPU/APU/IRQ smoke vectors, determinism, save-persistence round-trip, throughput.
    /// Run on hand-assembled vectors and a self-contained synthetic cartridge, so they need no external assets and run
    /// anywhere.</summary>
    A,
    /// <summary>Reference-ROM behavioural checks (jsmolka gba-tests, FuzzARM, the mGBA test suite, the AGS aging
    /// cartridge, deterministic render-hash floors). Need the external test corpus and/or a real replacement BIOS; they
    /// skip (never fail) when those are absent.</summary>
    B,
    /// <summary>Cross-machine link determinism — two-console SIO link-lock and replay-through-churn. Reserved; no stages
    /// yet (the GBA core ships a null link, so a standalone console cannot exercise this).</summary>
    C,
}
