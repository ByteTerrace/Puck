using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One journal entry — the tick and engine tick a mutation applied at, and the mutation itself, which the
/// replay reproduces.</summary>
/// <param name="Tick">The simulation-tick coordinate undo and replay rebase a <c>Cycle</c> epoch against.</param>
/// <param name="EngineTick">The engine-tick coordinate they rebase an <c>Advance</c> epoch against.</param>
/// <param name="Mutation">The applied mutation.</param>
/// <remarks>The two clocks are independent.</remarks>
public readonly record struct WorldJournalEntry(ulong Tick, ulong EngineTick, WorldMutation Mutation) {
    internal MutationDrawHistory? DrawHistory { get; init; }

    internal static bool MayDraw(WorldDefinition current, WorldMutation mutation) =>
        current.StateRaw?.DrawInstallCount > 0 || mutation is WorldMutation.Batch or WorldMutation.UpsertStateRow { Row.Draw.PrivateStream: true };

    internal bool TryCompose(WorldServer host, WorldDefinition current, out WorldDefinition candidate, out string reason) {
        current = DrawHistory?.RestorePositions(current) ?? current;
        using var input = DrawHistory is not null || MayDraw(current, Mutation)
            ? host.DrawKeys.Replay(DrawHistory?.Words ?? [], static address => new InvalidDataException($"PrivateDrawJournalMismatch: {address}"), historical: true)
            : null;
        try {
            return WorldDocument.TryCompose(current, Mutation, Tick, EngineTick, host.InstanceIdentity, out candidate, out reason, out _, out _, host.GrantTable);
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
