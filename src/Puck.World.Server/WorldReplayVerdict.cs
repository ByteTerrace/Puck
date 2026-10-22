using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The outcome of comparing a recording's LIVE per-tick hash trace against a fresh re-drive's trace: the verdict is
/// data, not a bare <see langword="bool"/>, so a caller reports WHERE a replay stopped matching rather than only THAT
/// it did. <see cref="DivergedAt"/> is the first differing tick, or <c>-1</c> when the two traces are identical.
/// </summary>
/// <remarks>
/// The divergence tick separates two failures a tail-hash comparison folds together. Diverging at tick 0 means the
/// starting state differs — the boot image or the start checkpoint the fresh world restored is not where the live
/// session stood when the capture armed. Diverging later means the starting state matched and the trajectory drifted
/// afterwards, which is a genuine determinism defect rather than a capture-boundary artifact. Machine cores are
/// outside the per-tick trace; a tape pins them once, at its end, and <see cref="MachineDivergence"/> names a core
/// the re-drive left in another state.
/// </remarks>
/// <param name="Ticks">The number of recorded ticks compared.</param>
/// <param name="Recorded">The LIVE session's tail hash — the state the running world actually reached.</param>
/// <param name="Replayed">The fresh re-drive's tail hash.</param>
/// <param name="DivergedAt">The first tick at which the traces differ, or <c>-1</c> when they never do.</param>
/// <param name="DivergentComponents">The authoritative hash components that differ at <paramref name="DivergedAt"/>,
/// in fold order, when the tape carries the per-component trace; <see langword="null"/> when it does not.</param>
/// <param name="MachinesCompared">How many machine cores the tape pins at its end and the comparison read; zero when
/// the tape pins none.</param>
/// <param name="MachineDivergence">The first way the re-drive's cores disagree with the recorded digests, or
/// <see langword="null"/> when every pinned core matches or none is pinned.</param>
public readonly record struct WorldReplayVerdict(int Ticks, ulong Recorded, ulong Replayed, int DivergedAt, IReadOnlyList<WorldStateHashComponent>? DivergentComponents = null, int MachinesCompared = 0, string? MachineDivergence = null) {
    /// <summary>Gets whether the traces differ from the very first tick, which indicts the starting state rather than
    /// the simulation's trajectory.</summary>
    public bool DivergedAtStart => (DivergedAt == 0);
    /// <summary>Gets whether the fresh re-drive reproduced the live session on EVERY tick and left every pinned machine
    /// core in the recorded state.</summary>
    public bool Match => ((DivergedAt < 0) && (MachineDivergence is null));

    /// <summary>Renders the shared verdict fragment both replay verbs report, naming the divergence tick when there is
    /// one, the components that differ there when the comparison could read them, and the machine cores' own
    /// verdict when the tape pins them.</summary>
    /// <returns>The verdict text.</returns>
    public string Describe() {
        var machines = ((MachineDivergence is { } divergence)
            ? $" | {divergence}"
            : ((MachinesCompared > 0)
                ? $" | machine cores MATCH ({MachinesCompared})"
                : string.Empty));

        if (Match) {
            return $"MATCH | {Ticks} ticks | hash=0x{Recorded:X16}{machines}";
        }

        if (DivergedAt < 0) {
            return $"MISMATCH in the machine cores alone | {Ticks} ticks | hash=0x{Recorded:X16}{machines}";
        }

        var where = (DivergedAtStart
            ? "at tick 0 (the starting state itself differs)"
            : $"first at tick {DivergedAt} of {Ticks} (the starting state matched)"
        );
        var components = ((DivergentComponents is { Count: > 0 } differing)
            ? $" | differing components: {string.Join(separator: ", ", values: differing)}"
            : string.Empty);

        return $"MISMATCH {where} | live tail=0x{Recorded:X16} replayed=0x{Replayed:X16}{components}{machines}";
    }
}
