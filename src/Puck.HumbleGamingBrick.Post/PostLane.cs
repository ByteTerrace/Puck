namespace Puck.HumbleGamingBrick.Post;

/// <summary>Which ledger rows a run measures. Membership is derived from each row's recorded outcome, never listed by
/// hand: a suite whose rows all pass is entirely in the gate, and a row moves lanes the moment its recorded outcome
/// does.</summary>
internal enum PostLane {
    /// <summary>Every row.</summary>
    All,
    /// <summary>The rows recorded as passing, plus any row with no record (which fails as unrecorded either way) and the
    /// unrunnable rows (which cost nothing). A passing case exits at its signature after a handful of frames, so this is
    /// the cheap lane and the one that must stay green.</summary>
    Gate,
    /// <summary>The rows recorded as failing or inconclusive, plus the unrunnable rows. These run to their frame cap by
    /// construction and are re-measured to find a ratchet (a recorded fail that now passes) or a changed pixel count.</summary>
    Frontier,
}
