
namespace Puck.World.Server;

public sealed partial class WorldTick {
    // The authority's half of music.state: the clock every listener's score steps on, read where the rules read it.
    // No music section authored reads as a fixed, honest "no music" line rather than an empty/refused answer, matching
    // audio.state's "device=unsupported" posture for an absent capability.
    internal string DescribeMusicClock() => ((m_musicClock is { } clock)
        ? $"[music.clock: elapsedTicks={clock.ElapsedTicks} phaseError={ReadClock(facet: ClockFacet.PhaseError)} beat={clock.CurrentBeat} bar={clock.CurrentBar}]"
        : "[music.clock: none declared]");
    // $clock:<music>:phaseError|beat|bar|beatInBar read the clock's CURRENT position, not the tick argument every other
    // operand reads through: MusicClock's own domain is engine ticks, already advanced this Step by the time a rule
    // fires. The phase error is the signed tick distance to the nearest beat, positive after the beat, negative ahead of
    // the next one, tied toward "after" at exactly half a beat; beat and bar are indices since the music began, and
    // beatInBar is zero-based. A world with no music row has no clock and reads 0: the compiler already refuses these
    // operands for such a world, so a live read only ever reaches here with one declared.
    internal long ReadClock(ClockFacet facet) {
        if (m_musicClock is not { } clock) {
            return 0L;
        }

        switch (facet) {
            case ClockFacet.Beat:
                return ((long)clock.CurrentBeat);
            case ClockFacet.Bar:
                return ((long)clock.CurrentBar);
            case ClockFacet.BeatInBar:
                return ((long)(clock.CurrentBeat % ((ulong)clock.BeatsPerBar)));
        }

        var ticksPerBeat = ((ulong)clock.TicksPerBeat);
        var remainder = (clock.ElapsedTicks % ticksPerBeat);

        return ((remainder <= (ticksPerBeat / 2UL))
            ? (long)remainder
            : (((long)remainder) - ((long)ticksPerBeat))
        );
    }
}
