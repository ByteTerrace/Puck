namespace Puck.World;

/// <summary>
/// The outcome of comparing a recording's LIVE per-tick hash trace against a fresh re-drive's trace: the verdict is
/// data, not a bare <see langword="bool"/>, so a caller reports WHERE a replay stopped matching rather than only THAT
/// it did. <see cref="DivergedAt"/> is the first differing tick, or <c>-1</c> when the two traces are identical.
/// </summary>
/// <remarks>
/// The divergence tick separates two failures a tail-hash comparison folds together. Diverging at tick 0 means the
/// starting state differs — the fresh world rebuilds from the definition's boot image, so a capture armed after the
/// live session had already moved cannot reproduce it. Diverging later means the starting state matched and the
/// trajectory drifted afterwards, which is a genuine determinism defect rather than a capture-boundary artifact.
/// </remarks>
/// <param name="Ticks">The number of recorded ticks compared.</param>
/// <param name="Recorded">The LIVE session's tail hash — the state the running world actually reached.</param>
/// <param name="Replayed">The fresh re-drive's tail hash.</param>
/// <param name="DivergedAt">The first tick at which the traces differ, or <c>-1</c> when they never do.</param>
public readonly record struct WorldReplayVerdict(int Ticks, ulong Recorded, ulong Replayed, int DivergedAt) {
    /// <summary>Gets whether the fresh re-drive reproduced the live session on EVERY tick.</summary>
    public bool Match => DivergedAt < 0;

    /// <summary>Gets whether the traces differ from the very first tick, which indicts the starting state rather than
    /// the simulation's trajectory.</summary>
    public bool DivergedAtStart => DivergedAt == 0;

    /// <summary>Renders the shared verdict fragment both replay verbs report, naming the divergence tick when there is
    /// one.</summary>
    /// <returns>The verdict text.</returns>
    public string Describe() {
        if (Match) {
            return $"MATCH | {Ticks} ticks | hash=0x{Recorded:X16}";
        }

        var where = (DivergedAtStart
            ? "at tick 0 (the starting state itself differs)"
            : $"first at tick {DivergedAt} of {Ticks} (the starting state matched)");

        return $"MISMATCH {where} | live tail=0x{Recorded:X16} replayed=0x{Replayed:X16}";
    }
}
