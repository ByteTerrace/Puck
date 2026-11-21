using Puck.Abstractions.Machines;
using Puck.Maths;

namespace Puck.Machines;

/// <summary>What a host does around each part of a segment <see cref="MachineSegment.Run"/> executes: note where a part
/// starts, and publish what it completed.</summary>
public interface IMachineSegmentObserver {
    /// <summary>Notes the instant a part starts at, before its input is applied and its cycles run.</summary>
    void PartStarting();
    /// <summary>Publishes what a part completed, with the core standing at an instruction boundary.</summary>
    /// <param name="positionTicks">The engine ticks of the segment run so far: the segment's whole budget after its last
    /// part.</param>
    /// <param name="presents">Whether the part's frames are for display: <see langword="false"/> in every repetition of
    /// a fast-forwarded segment but the last.</param>
    void PartCompleted(ulong positionTicks, bool presents);
}
/// <summary>
/// The one way a host executes an accepted segment, shared by the single-machine worker and the cable-linked group: the
/// segment's engine-tick budget becomes cycles through the host's remainder-carrying phase, and the cycles run in parts
/// that end where a seat's controller image changes and where the machine completes a frame, so a press lands on the
/// native frame it happened in and every native frame can be copied whole.
/// <para>
/// Invariant: dividing a segment never changes where the machine ends up. The parts' budgets are taken from the same
/// phase, so they sum to the whole segment's budget, and a core that measures each budget from where its clock stands
/// (<see cref="ITimeTravelMachineCore{TInput}.CarriesBudgetOvershoot"/> false) is given each part less what the part
/// before it overran, so its last part ends at the instruction boundary the undivided budget ends at. With no change
/// inside the segment the input is applied once, as an undivided segment applies it. This method is the only place a
/// segment is divided; a host reaches it only through <see cref="QueuedMachineWorker"/> or
/// <see cref="LinkedMachineGroup"/>, and the shared Post stage <c>queued-host-segment-parts</c> holds each core to the
/// invariant by running the same segments divided and whole.
/// </para>
/// </summary>
public static class MachineSegment {
    /// <summary>Runs one accepted segment, once per fast-forward repetition, and records each part for rewind.</summary>
    /// <typeparam name="TObserver">The host's publication, a value type so the calls devirtualize.</typeparam>
    /// <param name="core">The core, or the group core, to advance.</param>
    /// <param name="timeTravel">The rewind and fast-forward layer riding the core, or <see langword="null"/>.</param>
    /// <param name="phase">The host's tick-to-cycle phase, advanced by the segment's ticks per repetition.</param>
    /// <param name="deltaTicks">The segment's engine-tick budget; above zero.</param>
    /// <param name="input">The seat image held at the segment's start.</param>
    /// <param name="changes">The seats' changes inside the segment, in rising offset order. A change at or past the
    /// segment's end is not applied here.</param>
    /// <param name="divides">Whether the segment runs in parts. <see langword="false"/> is the reference the parts are
    /// held to: one part, holding every seat's last image of the segment from its start.</param>
    /// <param name="observer">The host's publication around each part.</param>
    /// <returns>The seat image held when the segment ended.</returns>
    public static MachinePads Run<TObserver>(
        ITimeTravelMachineCore<MachinePads> core,
        MachineTimeTravel<MachinePads>? timeTravel,
        ref RationalRateAccumulator phase,
        ulong deltaTicks,
        in MachinePads input,
        ReadOnlySpan<MachineSeatChange> changes,
        bool divides,
        ref TObserver observer
    ) where TObserver : IMachineSegmentObserver {
        var carries = core.CarriesBudgetOvershoot;
        var factor = (timeTravel?.FastForwardFactor ?? 1);
        var held = input;

        // Fast-forward repeats the exact segment rather than folding the factor into one large budget, so every
        // rewind record owns the budget and phase that produced it.
        for (var repeat = 0; (repeat < factor); ++repeat) {
            var applied = false;
            var next = 0;
            var overrun = 0L;
            var position = 0UL;

            held = input;

            if (!divides) {
                foreach (ref readonly var change in changes) {
                    if (change.OffsetTicks < deltaTicks) {
                        held[change.Seat] = change.Pad;
                    }
                }

                next = changes.Length;
            }

            do {
                for (; ((next < changes.Length) && (changes[next].OffsetTicks <= position)); ++next) {
                    held[changes[next].Seat] = changes[next].Pad;
                    applied = false;
                }

                // The rate is read on the executing thread, so a rate that tracks emulated state (a clock-multiplier
                // latch) is read consistently with the cycles it gates.
                var rate = core.CycleRate;
                var cut = deltaTicks;

                if (divides) {
                    if (
                        (next < changes.Length) &&
                        (changes[next].OffsetTicks < cut)
                    ) {
                        cut = changes[next].OffsetTicks;
                    }

                    // One tick past the frame's end, so the cycles a carried overshoot already spent of this part's
                    // budget do not leave the part an instruction short of it.
                    var toFrameEnd = phase.TicksForCycles(
                        cycles: Math.Max(
                            val1: 1L,
                            val2: core.CyclesToFrameEnd
                        ),
                        rate: rate
                    );

                    if (toFrameEnd < ((cut - position) - 1UL)) {
                        cut = ((position + toFrameEnd) + 1UL);
                    }
                }

                var run = (phase.TakeCycleBudget(
                    rate: rate,
                    ticks: (cut - position)
                ) - overrun);

                observer.PartStarting();

                if (!applied) {
                    core.ApplyInput(input: in held);
                    applied = true;
                }

                // Every part reaches the core, a part whose budget an overrun swallowed with nothing to run. What the
                // clock moved past the part's own budget is what the next part gives back.
                if (carries) {
                    core.RunCycles(cycles: run);
                } else {
                    var before = core.CycleCount;

                    core.RunCycles(cycles: Math.Max(
                        val1: 0L,
                        val2: run
                    ));
                    overrun = ((core.CycleCount - before) - run);
                }

                position = cut;

                // A part an overrun swallowed ran nothing and records nothing, except the segment's last, which is the
                // one instant of the segment a rewind lands on and carries the phase that landing restores.
                if (
                    (run > 0L) ||
                    (position == deltaTicks)
                ) {
                    timeTravel?.Record(
                        budget: Math.Max(
                            val1: 0L,
                            val2: run
                        ),
                        hostAccumulator: phase,
                        input: in held,
                        lands: (position == deltaTicks)
                    );
                }

                observer.PartCompleted(
                    positionTicks: position,
                    presents: (repeat == (factor - 1))
                );
            } while (position < deltaTicks);
        }

        return held;
    }
}
