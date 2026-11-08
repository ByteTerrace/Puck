namespace Puck.World.Server;

// The retained turn journal is the arena host's: this host forwards to it, so the rewound-group suppression a rule
// group reads is the one the arena host keeps, advanced to the same tick by AdvanceHost.
public sealed partial class WorldRuleHost {
    /// <inheritdoc />
    public void ConfigureUndo(IReadOnlyList<ArenaUndoPlan> plans) => Host.ArenaHost.ConfigureUndo(plans: plans);
    /// <inheritdoc />
    public bool UndoTurnPending(string group) => Host.ArenaHost.UndoTurnPending(group: group);
    /// <inheritdoc />
    public void BeginUndoTurn(string group) => Host.ArenaHost.BeginUndoTurn(group: group);
    /// <inheritdoc />
    public void BeginUndoPass(string group) => Host.ArenaHost.BeginUndoPass(group: group);
    /// <inheritdoc />
    public void EndUndoPass(string group) => Host.ArenaHost.EndUndoPass(group: group);
    /// <inheritdoc />
    public void CommitUndoTurn(string group) => Host.ArenaHost.CommitUndoTurn(group: group);
    /// <inheritdoc />
    public void CancelUndoTurn(string group) => Host.ArenaHost.CancelUndoTurn(group: group);
    /// <inheritdoc />
    public bool TryRewindGroup(string group, out string reason) {
        if (!Host.ArenaHost.TryRewindGroup(group: group, reason: out reason)) {
            return false;
        }
        InvalidateArenaScheduling();
        return true;
    }
    /// <inheritdoc />
    public bool UndoGroupSuppressed(string group, ulong tick) => Host.ArenaHost.UndoGroupSuppressed(group: group, tick: tick);
}
