namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    private readonly HashSet<string> m_rewoundUndoGroups = new(comparer: StringComparer.Ordinal);
    private ulong m_rewoundUndoTick = ulong.MaxValue;

    internal void BeginUndoEvaluationTick(ulong tick) {
        if (m_rewoundUndoTick != tick) {
            m_rewoundUndoGroups.Clear();
        }
        m_rewoundUndoTick = tick;
    }

    /// <inheritdoc />
    public void ConfigureUndo(IReadOnlyList<ArenaUndoPlan> plans) => Host.Arena.ConfigureUndo(plans);
    /// <inheritdoc />
    public bool UndoTurnPending(string group) => Host.Arena.UndoTurnPending(group);
    /// <inheritdoc />
    public void BeginUndoTurn(string group) => Host.Arena.BeginUndoTurn(group);
    /// <inheritdoc />
    public void BeginUndoPass(string group) => Host.Arena.BeginUndoPass(group);
    /// <inheritdoc />
    public void EndUndoPass(string group) => Host.Arena.EndUndoPass(group);
    /// <inheritdoc />
    public void CommitUndoTurn(string group) => Host.Arena.CommitUndoTurn(group);
    /// <inheritdoc />
    public void CancelUndoTurn(string group) => Host.Arena.CancelUndoTurn(group);
    /// <inheritdoc />
    public bool TryRewindTurn(string group, out string reason) {
        if (!Host.Arena.TryRewindTurn(group, out reason)) {
            return false;
        }
        InvalidateArenaScheduling();
        if (m_rewoundUndoTick != Tick) {
            m_rewoundUndoGroups.Clear();
            m_rewoundUndoTick = Tick;
        }
        _ = m_rewoundUndoGroups.Add(item: group);
        return true;
    }
    /// <inheritdoc />
    public bool UndoGroupSuppressed(string group, ulong tick) => ((m_rewoundUndoTick == tick) && m_rewoundUndoGroups.Contains(item: group));
}
