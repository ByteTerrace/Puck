using Puck.Abstractions.Waiting;

namespace Puck.Abstractions.Machines;

/// <summary>
/// A cable-linked group of <see cref="IMachineRuntime"/>s advanced as ONE deterministically interleaved unit — a LINK is
/// an object, not a relationship between two peers. The engine that owns the machines creates it (see
/// <see cref="IMachineLinkingEngine.TryLink"/>); a host steps the link once per frame INSTEAD of stepping its members
/// individually, so the engine's own deterministic interleave — not the host's frame order — decides who runs when.
/// Disposing the link severs the cable; its members then step independently again (an unfinished external-clock transfer
/// stays pending, as on unplugged hardware).
/// </summary>
public interface IMachineLink : IDisposable {
    /// <summary>Gets the linked machines, in cable order — the same instances handed to
    /// <see cref="IMachineLinkingEngine.TryLink"/>. A host that steps a link skips these from its per-machine step loop.</summary>
    IReadOnlyList<IMachineRuntime> Machines { get; }
    /// <summary>Gets the number of serial bytes exchanged across the cable since the link formed — the pipe-assertable
    /// liveness signal a host echoes (a live link's count climbs; a dormant one never advances). Nothing depends on the
    /// bytes themselves.</summary>
    long CompletedTransfers { get; }

    /// <summary>Advances every linked machine forward by one shared step budget, interleaved deterministically by the
    /// engine's own pair-stepper. Each member is driven with its own controller image (by cable order), so a
    /// multiplayer link still routes per-seat input.</summary>
    /// <param name="deltaTicks">The shared fixed-step tick budget for this frame.</param>
    /// <param name="inputs">Each member's controller image, in cable order (length equals <see cref="Machines"/>).</param>
    void Step(ulong deltaTicks, ReadOnlySpan<MachinePadState> inputs);

    /// <summary>Steps an inline link, or submits and resumes a queued link's ordered drain.</summary>
    QueuedMachineSubmission StepSegment(ulong deltaTicks, ReadOnlySpan<MachinePadState> inputs, ref ulong generation, WaitDeadline deadline) {
        Step(deltaTicks, inputs);
        return QueuedMachineSubmission.Accepted;
    }
    /// <summary>Steps or submits one segment whose members' controller images change inside it, stamped with the host
    /// clock position it ends at (<see cref="IQueuedMachineRuntime.SetSegmentClock"/>). A link that keeps no time inside
    /// a segment holds each member's last image for the whole of it, which is the default.</summary>
    /// <param name="deltaTicks">The shared fixed-step tick budget.</param>
    /// <param name="inputs">Each member's controller image at the segment's start, in cable order.</param>
    /// <param name="changes">The members' changes inside the segment, in rising offset order; a seat is a cable
    /// position.</param>
    /// <param name="endTicks">The host clock position at the segment's end, in engine ticks, or
    /// <see langword="null"/> for the link's own running clock.</param>
    /// <param name="generation">The drain generation a resumed step hands back.</param>
    /// <param name="deadline">The last instant to wait.</param>
    /// <returns>The observable submission outcome.</returns>
    QueuedMachineSubmission StepSegment(ulong deltaTicks, ReadOnlySpan<MachinePadState> inputs, ReadOnlySpan<MachineSeatChange> changes, ulong? endTicks, ref ulong generation, WaitDeadline deadline) {
        if (changes.IsEmpty) {
            return StepSegment(deltaTicks, inputs, ref generation, deadline);
        }

        var held = new MachinePadState[inputs.Length];

        inputs.CopyTo(destination: held);

        foreach (ref readonly var change in changes) {
            held[change.Seat] = change.Pad;
        }

        return StepSegment(deltaTicks, held, ref generation, deadline);
    }
}
