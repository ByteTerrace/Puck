namespace Puck.World.Server;

/// <summary>What an arena install keeps of the scoped replays bound to the arena it replaces or reloads.</summary>
public enum WorldArenaContinuity {
    /// <summary>A mutation's install: each scope keeps its starting image and tape while every one of its rows keeps
    /// its declaration, and drops them, refusing its drive by name, when the install re-declared one.</summary>
    Edit,
    /// <summary>A whole-document load, rebuild or checkpoint restore: the document's state replaces the arena's, so
    /// every scope drops its starting image and tape (a checkpoint then restores its own).</summary>
    Load,
}

public sealed partial class WorldServer {
    private IReadOnlyList<WorldScopedReplay>? m_scopedSources;
    private WorldScopedReplayRuntime[] m_scopedReplays = [];

    // Hot observers enumerate the array through a span, never an interface's boxed enumerator.
    internal ReadOnlySpan<WorldScopedReplayRuntime> ScopedReplaySpan {
        get { _ = ScopedReplays; return m_scopedReplays; }
    }

    /// <summary>Gets the authored scopes. They outlive arena replacements, which decide what each keeps
    /// (<see cref="WorldArenaContinuity"/>); a change to the replays section binds fresh scopes.</summary>
    public IReadOnlyList<WorldScopedReplayRuntime> ScopedReplays {
        get {
            if (!ReferenceEquals(objA: m_scopedSources, objB: Definition.ReplaysRaw)) {
                m_scopedSources = Definition.ReplaysRaw;
                m_scopedReplays = (Definition.ReplaysRaw ?? []).Select(selector: source => new WorldScopedReplayRuntime(server: this, source: source)).ToArray();
            }
            return m_scopedReplays;
        }
    }

    // The one place an arena install decides what the scopes keep, called with the arena about to be live. Scopes
    // bound to a replays section the installed document no longer holds are left alone: the next read binds fresh ones.
    private void ContinueScopes(StateArena? previous, StateArena arena, WorldArenaContinuity continuity) {
        if ((previous is null) || !ReferenceEquals(objA: m_scopedSources, objB: Definition.ReplaysRaw)) { return; }
        foreach (var scope in m_scopedReplays) { scope.Continue(arena: arena, continuity: continuity, previous: previous); }
    }
}
