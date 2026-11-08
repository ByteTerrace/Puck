namespace Puck.World.Server;

/// <summary>What an arena install keeps of the rule latches and scoped replays bound to the arena it replaces or
/// reloads.</summary>
public enum WorldArenaContinuity {
    /// <summary>A mutation's install: the rule latch keeps every entry, a key-bound one moved to its key name's ordinal
    /// in a replacement arena, and each scope keeps its starting image and tape while every one of its rows keeps its
    /// declaration, dropping them, refusing its drive by name, when the install re-declared one.</summary>
    Edit,
    /// <summary>A whole-document load, rebuild or checkpoint restore: the document's state replaces the arena's, so the
    /// rule and interaction latches start empty and every scope drops its starting image and tape (a checkpoint then
    /// restores its own of each).</summary>
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

    // The one place an arena install decides what survives it, called with the arena about to be live: the rule latches
    // and the scopes. Scopes bound to a replays section the installed document no longer holds are left alone: the next
    // read binds fresh ones.
    private void ContinueArena(StateArena? previous, StateArena arena, WorldArenaContinuity continuity) {
        if (previous is null) { return; }
        m_ruleHost.ContinueLatches(arena: arena, continuity: continuity, previous: previous);
        if (!ReferenceEquals(objA: m_scopedSources, objB: Definition.ReplaysRaw)) { return; }
        foreach (var scope in m_scopedReplays) { scope.Continue(arena: arena, continuity: continuity, previous: previous); }
    }
}
