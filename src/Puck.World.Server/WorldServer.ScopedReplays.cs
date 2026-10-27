namespace Puck.World.Server;

public sealed partial class WorldServer {
    private StateArena? m_scopedArena;
    private IReadOnlyList<WorldScopedReplay>? m_scopedSources;
    private WorldScopedReplayRuntime[] m_scopedReplays = [];

    // Hot observers enumerate the array through a span, never an interface's boxed enumerator.
    internal ReadOnlySpan<WorldScopedReplayRuntime> ScopedReplaySpan {
        get { _ = ScopedReplays; return m_scopedReplays; }
    }

    /// <summary>Gets the authored scopes bound to this arena. A rebuild drops their derived recordings.</summary>
    public IReadOnlyList<WorldScopedReplayRuntime> ScopedReplays {
        get {
            if (!ReferenceEquals(objA: m_scopedArena, objB: m_arena) || !ReferenceEquals(objA: m_scopedSources, objB: Definition.ReplaysRaw)) {
                m_scopedArena = m_arena;
                m_scopedSources = Definition.ReplaysRaw;
                m_scopedReplays = (Definition.ReplaysRaw ?? []).Select(selector: source => new WorldScopedReplayRuntime(server: this, source: source)).ToArray();
            }
            return m_scopedReplays;
        }
    }
}
