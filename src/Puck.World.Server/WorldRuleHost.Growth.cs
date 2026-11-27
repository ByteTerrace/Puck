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
        arena.GrowthAdmission = new GrowthAdmission(arena: arena, host: this).Admit;
    }
    // Admits a growth of the arena the host drives against the tick's growth budget. The host owns its arena; the arena
    // must not own the host, whose server any holder of the arena (a reader's binding, a projection's bound values)
    // would otherwise keep alive with it. While the host runs it is reachable through its server, so the weak reference
    // always resolves for the host's own growth; an arena whose host is gone has no budget left to judge by and admits.
    private sealed class GrowthAdmission(StateArena arena, WorldRuleHost host) {
        private readonly WeakReference<WorldRuleHost> m_host = new(target: host);

        public string? Admit(ArenaLayout proposed) => (m_host.TryGetTarget(target: out var target) ? target.AdmitGrowth(arena: arena, proposed: proposed) : null);
    }
    private string? AdmitGrowth(StateArena arena, ArenaLayout proposed) {
        BeginGrowthTick(engineTick: Host.CompletedEngineTicks);
        var spent = (arena.ReservationWorkCharged - m_growthWorkStart);

        // Earlier growth in an open scope may also need rolling back after this proposal.
        return m_growthCompilation?.RefuseReservation(growthWork: (2L * (spent + StateArena.ReservationWork(layout: proposed))));
    }
}
