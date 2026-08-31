using Puck.Abstractions.Machines;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Folds every currently engaged diegetic instrument's authored tempo into an ADDITIONAL beat-boundary
    /// signal for THIS step, alongside (never in place of) <see cref="m_musicClock"/>'s own compiled boundary — every
    /// segment/transition authored against the world's own tempo keeps working unchanged. Simply holding a
    /// <see cref="GrantSubjectKind.Screen"/> <see cref="ControlApplication"/> onto an <see cref="IInstrumentClockSource"/>
    /// machine is the whole gate: <c>WorldSessionLever</c> is architecturally barred from feeding simulation state
    /// (its own remarks: "a knob the simulation reads is a document mutation, not a lever"), while a
    /// <see cref="ControlApplication"/> is ALREADY a fully deterministic, tape-covered fact
    /// (<c>WorldCommand.ComposeControl</c>/<c>DissolveControl</c>, the same ordered domain every other authority
    /// command travels through) — reusing it needs no new mutation kind. Never contributes
    /// <see cref="Puck.Audio.Simulation.MusicClockBoundary.Bar"/>: a per-row authored tempo carries no bar-length
    /// convention to derive one honestly from. Reuses <see cref="m_musicClock"/>'s own already-checkpointed
    /// <c>ElapsedTicks</c> as the shared time base, so this fold needs no checkpoint/replay surface of its own — it
    /// is a pure function of (checkpointed clock position, the engaged instrument's static authored tempo) recomputed
    /// fresh every tick.</summary>
    /// <param name="previousElapsedTicks">The music clock's elapsed ticks before this step's <c>Advance</c>.</param>
    /// <param name="currentElapsedTicks">The music clock's elapsed ticks after this step's <c>Advance</c>.</param>
    private Puck.Audio.Simulation.MusicClockBoundary InstrumentClockBoundary(ulong previousElapsedTicks, ulong currentElapsedTicks) {
        var boundary = Puck.Audio.Simulation.MusicClockBoundary.None;

        for (var slot = 0; (slot < Population.LocalSeatCount); slot++) {
            if (
                (ResolveEngagedScreenIndex(seatSlot: slot) is { } screenIndex) &&
                (m_machines.InstrumentTicksPerBeat(index: screenIndex) is { } ticksPerBeat)
            ) {
                var previousRow = (previousElapsedTicks / ((ulong)ticksPerBeat));
                var currentRow = (currentElapsedTicks / ((ulong)ticksPerBeat));

                if (currentRow != previousRow) {
                    boundary |= Puck.Audio.Simulation.MusicClockBoundary.Beat;
                }
            }
        }

        return boundary;
    }
    /// <summary>Returns the engine screen index the local seat currently holds a <see cref="GrantSubjectKind.Screen"/>
    /// application to, or <see langword="null"/> when the slot is out of range or the seat holds no screen
    /// application. A seat with more than one screen application (never authored today) reports the first, in the
    /// set's own order.</summary>
    /// <param name="seatSlot">The 0-based local seat slot.</param>
    private int? ResolveEngagedScreenIndex(int seatSlot) {
        if ((seatSlot < 0) || (seatSlot >= Population.LocalSeatCount)) {
            return null;
        }

        foreach (var application in Grants.Applications(principal: WorldPrincipal.Seat(slot: seatSlot))) {
            if (application.Target.Kind == GrantSubjectKind.Screen) {
                return application.Target.Value;
            }
        }

        return null;
    }
    // The instrument.state echo: which screen (if any) the routed seat is engaged with, whether that screen carries
    // an instrument machine, and its authored tempo — "driving" always mirrors "instrument=yes" today, since holding
    // the application IS the clock-fold gate (see InstrumentClockBoundary's own remarks); the field stays a distinct
    // line rather than folding into "instrument=yes" because it names the FACT this verb exists to answer, not an
    // implementation detail of how the gate happens to work today.
    private string DescribeInstrumentState(int seatSlot) {
        if (ResolveEngagedScreenIndex(seatSlot: seatSlot) is not { } screenIndex) {
            return "[instrument.state: none engaged]";
        }

        if (m_machines.InstrumentTicksPerBeat(index: screenIndex) is not { } ticksPerBeat) {
            return $"[instrument.state: screen={screenIndex} instrument=no]";
        }

        return $"[instrument.state: screen={screenIndex} instrument=yes ticksPerBeat={ticksPerBeat} driving=y]";
    }
}
