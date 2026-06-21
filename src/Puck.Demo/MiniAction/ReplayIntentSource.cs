namespace Puck.Demo.MiniAction;

/// <summary>
/// An <see cref="IPlayerIntentSource"/> that replays a recording's per-tick intents verbatim. Paired with a
/// <see cref="ReplayRosterEventSource"/> over the same recording and a world seeded with <see cref="Seed"/>, it
/// reproduces the recorded session exactly — the day-one replay guarantee, now through join/leave.
/// </summary>
public sealed class ReplayIntentSource : IPlayerIntentSource {
    private readonly MiniActionReplay m_replay;

    /// <summary>Initializes the source from a recording.</summary>
    public ReplayIntentSource(MiniActionReplay replay) {
        ArgumentNullException.ThrowIfNull(replay);

        m_replay = replay;
    }

    /// <summary>The recording's seed (the world must be created with it for an exact reproduction).</summary>
    public uint Seed => m_replay.Seed;
    /// <summary>The players present before tick 0.</summary>
    public IReadOnlyList<Guid> InitialRoster => m_replay.InitialRoster;
    /// <summary>The number of recorded ticks.</summary>
    public int TickCount => m_replay.Ticks.Count;

    /// <inheritdoc/>
    public void BeginFrame(ulong firstTick) { }

    /// <inheritdoc/>
    public PlayerIntent[] CollectTick(ulong tick, IReadOnlyList<Guid> players) {
        return ((tick < (ulong)m_replay.Ticks.Count)
            ? m_replay.Ticks[(int)tick]
            : new PlayerIntent[MiniActionWorld.MaxPlayers]);
    }
}
