namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    private WorldRuleCompilation? m_growthCompilation;
    private StateArena? m_growthArena;
    private ulong m_growthTick = ulong.MaxValue;
    private long m_growthWorkStart;

    private void ConfigureGrowthAdmission() {
        if (Arena is not { } arena) { return; }
        if (!ReferenceEquals(objA: arena, objB: m_growthArena)) {
            m_growthArena = arena;
            m_growthTick = ulong.MaxValue;
        }
        arena.GrowthAdmission = proposed => {
            if (m_growthTick != EngineTick) {
                m_growthTick = EngineTick;
                m_growthWorkStart = arena.ReservationWorkCharged;
            }
            var spent = arena.ReservationWorkCharged - m_growthWorkStart;
            return m_growthCompilation?.RefuseReservation(layout: proposed, growthWork: spent + (2L * StateArena.ReservationWork(layout: proposed)));
        };
    }
}
