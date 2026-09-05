
namespace Puck.World.Server;

public sealed partial class WorldServer {
    // The music.state echo: segment, any pending transition, the most recent committed transition's tick/from/to
    // (none= before the first one), the currently active conditional-layer tune ids, and the most recent
    // embellishment's patch/tick. No music section authored reads as a fixed, honest "no music" line rather than an
    // empty/refused answer, matching audio.state's "device=unsupported" posture for an absent capability.
    private string DescribeMusicState() {
        if (
            (m_musicClock is not { } clock) ||
            (m_musicDirector is not { } director)
        ) {
            return "[music.state: none declared]";
        }

        var layers = ((director.ActiveLayerTuneIds.Count > 0)
            ? string.Join(separator: ",", values: director.ActiveLayerTuneIds)
            : "none"
        );

        return $"[music.state: segment={director.CurrentSegmentId} pending={(director.PendingSegmentId ?? "none")} elapsedTicks={clock.ElapsedTicks} transitions={director.TransitionCount} lastTick={(director.LastTransitionTick?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) ?? "none")} lastFrom={(director.LastTransitionFromSegmentId ?? "none")} lastTo={(director.LastTransitionToSegmentId ?? "none")} layers={layers} lastEmbellishment={(director.LastEmbellishmentPatchId ?? "none")} lastEmbellishmentTick={(director.LastEmbellishmentTick?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) ?? "none")} phaseError={ReadClockPhaseError()}]";
    }
    // $clock:<music>:phaseError — the signed tick distance from the clock's CURRENT position (not the tick argument
    // every other operand reads through: MusicClock's own domain is engine ticks, already advanced this Step by the
    // time a rule fires) to the nearest beat. Positive after the beat, negative ahead of the next one, tied toward
    // "after" at exactly half a beat. A world with no music row has no clock and reads 0 — the compiler already
    // refuses this operand for such a world, so a live read only ever reaches here with one declared.
    private long ReadClockPhaseError() {
        if (m_musicClock is not { } clock) {
            return 0L;
        }

        var ticksPerBeat = (ulong)clock.TicksPerBeat;
        var remainder = (clock.ElapsedTicks % ticksPerBeat);

        return (remainder <= (ticksPerBeat / 2UL)) ? (long)remainder : ((long)remainder - (long)ticksPerBeat);
    }
}
