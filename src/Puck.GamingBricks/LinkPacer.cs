namespace Puck.GamingBricks;

/// <summary>The machines one <see cref="LinkPacer"/> interleave advances, addressed by cable position. Implement it
/// on a value type so the pacer's inner loop stays allocation-free and its calls devirtualize.</summary>
public interface ILinkPacerParticipants {
    /// <summary>Gets the number of participants, which is fixed for the length of one
    /// <see cref="LinkPacer.Run"/>.</summary>
    int Count { get; }

    /// <summary>Returns how far participant <paramref name="index"/> still is from its own cumulative pacing target,
    /// in that machine's clock units. Zero or negative means it has reached or overshot the target and is done for
    /// this budget; the pacer never treats an overshoot as a debt against another machine.</summary>
    /// <param name="index">The participant's cable position.</param>
    /// <returns>The signed remainder of the participant's target.</returns>
    long GetRemaining(int index);
    /// <summary>Advances participant <paramref name="index"/> by exactly one CPU step — an instruction, or the idle
    /// cycle a halted or bus-mastered machine takes in its place. Stepping any further would coarsen the interleave
    /// and let a peer observe more than one step of staleness across the cable.</summary>
    /// <param name="index">The participant's cable position.</param>
    void StepOnce(int index);
}
/// <summary>
/// The deterministic interleave every cable link advances its machines through, whatever medium or machine family
/// carries it: repeatedly step whichever machine is furthest behind its own cumulative pacing target, one CPU step at
/// a time, ties going to the lowest cable position, until no machine is behind. The rule is a pure function of the
/// participants' clocks and their targets — it holds no state of its own and reads no wall clock — so a linked run is
/// deterministic and replay-identical, and no machine ever observes a peer more than one step stale.
/// <para>
/// The tie-break and the one-step granularity are the contract, not implementation detail: they decide which machine
/// writes a cable word first, so changing either reorders the traffic of every recorded link replay. Targets are
/// cumulative and owned by the caller, which advances them by the shared budget before each run, so a step's
/// overshoot carries into the next budget instead of accreting into drift.
/// </para>
/// </summary>
public static class LinkPacer {
    /// <summary>Advances every participant until none is behind its target.</summary>
    /// <typeparam name="TParticipants">The participant set, a value type for an allocation-free loop.</typeparam>
    /// <param name="participants">The machines to interleave, in cable order.</param>
    public static void Run<TParticipants>(TParticipants participants) where TParticipants : ILinkPacerParticipants {
        var count = participants.Count;

        while (true) {
            var furthest = -1;
            var furthestRemaining = 0L;

            for (var index = 0; (index < count); ++index) {
                var remaining = participants.GetRemaining(index: index);

                // Strictly greater, scanned in cable order from a zero floor: the lowest index wins a tie, and a
                // participant that has reached or overshot its target is never selected.
                if (remaining > furthestRemaining) {
                    furthest = index;
                    furthestRemaining = remaining;
                }
            }

            if (furthest < 0) {
                return;
            }

            participants.StepOnce(index: furthest);
        }
    }
}
