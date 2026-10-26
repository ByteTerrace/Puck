namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    private WorldRuleCompilation? m_growthCompilation;
    private StateArena? m_growthArena;
    private ulong m_growthTick = ulong.MaxValue;
    private long m_growthWorkStart;

    private void BeginGrowthTick(ulong engineTick) {
        if (m_growthTick == engineTick) { return; }
        m_growthTick = engineTick;
        m_growthWorkStart = Arena.ReservationWorkCharged;
    }

    private void ConfigureGrowthAdmission() {
        if (Arena is not { } arena) { return; }
        if (!ReferenceEquals(objA: arena, objB: m_growthArena)) {
            m_growthArena = arena;
            m_growthTick = ulong.MaxValue;
        }
        arena.GrowthAdmission = proposed => {
            BeginGrowthTick(engineTick: Host.CompletedEngineTicks);
            var spent = arena.ReservationWorkCharged - m_growthWorkStart;
            // Earlier growth in an open scope may also need rolling back after this proposal.
            return m_growthCompilation?.RefuseReservation(layout: proposed, growthWork: 2L * (spent + StateArena.ReservationWork(layout: proposed)));
        };
    }
}
