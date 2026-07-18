namespace Puck.SdfVm.Views;

/// <summary>The priority BAND a screen-surface or view-stack claim is made at — lower numeric value always wins a
/// contested slot. Ties (two claims at the same band wanting the same slot) resolve to whichever claimed FIRST.
/// <para>
/// These are abstract bands, not a menu of named consumers. A claimant's identity lives entirely in its owner
/// token / registration name, never in this axis, so adding a new kind of claimant never means adding a new enum
/// member here. The three bands are spaced 10 apart so a future caller can insert an intermediate band (e.g.
/// <c>(Anchored + Overlay) / 2</c>) without renumbering anything, though in practice most claimants fit one of the
/// three as-is. Both screen-surface arbitration and <see cref="ViewStack"/> use this shared vocabulary.
/// </para></summary>
public enum ScreenSlotPriority {
    /// <summary>A claim tied to a fixed, load-bearing identity that owns its preferred slot outright — never evicted
    /// by anything in a lower band; only another <see cref="Anchored"/> claim for the SAME slot (a same-band race,
    /// e.g. a re-boot) can replace it. The room's booted cabinets claim this band.</summary>
    Anchored = 0,
    /// <summary>A session-scoped claim that temporarily borrows or overrides an <see cref="Anchored"/> slot for as
    /// long as some mode is active, then releases it — the room's creator-mode preview easel claims this band (the
    /// settled index-3 borrow contract).</summary>
    Overlay = 10,
    /// <summary>Any other floating, lowest-priority claim (an incidental diegetic screen, a placeable camera view) —
    /// the first band evicted/degraded when the room runs out of slots.</summary>
    Ambient = 20,
}
