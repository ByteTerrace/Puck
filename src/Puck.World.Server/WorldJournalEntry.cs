using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One journal entry — the tick and engine tick a mutation applied at, and the mutation itself, which the
/// replay reproduces.</summary>
/// <remarks>The two clocks are independent.</remarks>
public readonly record struct WorldJournalEntry {
    private readonly WorldMutation? m_mutation;
    /// <summary>The simulation tick an accepted edit applied at.</summary>
    public ulong Tick { get; }
    /// <summary>The engine tick an accepted edit applied at.</summary>
    public ulong EngineTick { get; }
    /// <summary>The mutation carried by the external-mutation recovery tail.</summary>
    public WorldMutation Mutation => m_mutation ?? throw new InvalidOperationException("The journal entry is a source edit.");

    public WorldJournalEntry(ulong Tick, ulong EngineTick, WorldMutation Mutation) {
        this.Tick = Tick;
        this.EngineTick = EngineTick;
        m_mutation = Mutation;
    }
    /// <summary>Unpacks a committed mutation from the mutation journal tap.</summary>
    public void Deconstruct(out ulong tick, out ulong engineTick, out WorldMutation mutation) {
        tick = Tick;
        engineTick = EngineTick;
        mutation = Mutation;
    }
    internal WorldJournalEntry(ulong tick, ulong engineTick, byte[] reloadPatch, WorldDrawPositions? positions) {
        Tick = tick;
        EngineTick = engineTick;
        ReloadPatch = reloadPatch;
        ReloadDrawPositions = positions;
    }

    internal MutationDrawHistory? DrawHistory { get; init; }
    internal byte[]? ReloadPatch { get; init; }
    internal WorldDrawPositions? ReloadDrawPositions { get; init; }
    internal string Description => m_mutation is { } mutation ? WorldServer.Describe(mutation) : "world.reload";
    internal object? Identity => (object?)m_mutation ?? ReloadPatch;

    internal static bool MayDraw(WorldDefinition current, WorldMutation mutation) =>
        current.StateRaw?.DrawInstallCount > 0 || mutation is WorldMutation.Batch or WorldMutation.UpsertStateRow { Row.Draw.PrivateStream: true };

    internal bool TryCompose(WorldServer host, WorldDefinition current, out WorldDefinition candidate, out string reason) {
        if (ReloadPatch is { } patch) {
            if (!WorldReloadDelta.TryApplyPatch(current, patch, current.DocumentDirectory, out var reloaded, out reason)) {
                candidate = current;
                return false;
            }
            candidate = reloaded;
            if (ReloadDrawPositions is { } positions &&
                (!WorldDrawBootResolver.TryResolve(candidate, host.InstanceIdentity, out candidate, out reason, host.DrawKeys, restore: positions) ||
                !WorldStateDocumentValues.TryResolve(candidate, out candidate, out reason))) { return false; }
            return true;
        }
        var mutation = Mutation;
        current = DrawHistory?.RestorePositions(current) ?? current;
        using var input = DrawHistory is not null || MayDraw(current, mutation)
            ? host.DrawKeys.Replay(DrawHistory?.Words ?? [], static address => new InvalidDataException($"PrivateDrawJournalMismatch: {address}"), historical: true)
            : null;
        try {
            return WorldDocument.TryCompose(current, mutation, Tick, EngineTick, host.InstanceIdentity, out candidate, out reason, out _, out _, host.GrantTable);
        } catch (InvalidDataException error) {
            candidate = current;
            reason = error.Message;
            return false;
        }
    }
}

// Rules can advance a site between two document edits. Recompose each accepted edit at its actual
// pre-draw cursor and masks, even when the intervening rule firings are outside the document journal.
internal sealed class MutationDrawHistory {
    internal sealed record Position(long Installation, long Cursor, ClosedBitset256[]? Masks);
    internal List<PrivateDrawWord> Words { get; } = [];
    internal Dictionary<long, Position> Positions { get; } = [];

    internal void Record(StateArena arena, PrivateDrawWord word) {
        Words.Add(word);
        if (Positions.ContainsKey(word.Installation)) { return; }
        for (var i = 0; i < arena.Rows.Count; i++) {
            if (arena.Rows[i].Draw?.Secret?.Installation == word.Installation) {
                Positions.Add(word.Installation, new(word.Installation, arena.DrawCursor(i), ArenaDraws.ReadMasks(arena, i)));
                break;
            }
        }
    }

    internal WorldDefinition RestorePositions(WorldDefinition current) {
        if (Positions.Count == 0) { return current; }
        var rows = current.AuthoredState.ToArray();
        for (var i = 0; i < rows.Length; i++) {
            if (rows[i].Draw?.Secret is { } site && Positions.TryGetValue(site.Installation, out var start)) {
                rows[i] = rows[i] with { DrawCursor = start.Cursor, DrawnMasks = start.Masks };
            }
        }
        return current.WithWorldState(rows);
    }
}
