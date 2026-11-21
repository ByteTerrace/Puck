using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One journal entry — the tick and engine tick a mutation or a source edit applied at, and the edit itself,
/// which the replay reproduces.</summary>
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
    internal WorldJournalEntry(ulong tick, ulong engineTick, byte[] reloadPatch, WorldDrawPositions? positions, Puck.Commands.Principal author) {
        Tick = tick;
        EngineTick = engineTick;
        ReloadPatch = reloadPatch;
        ReloadDrawPositions = positions;
        m_reloadAuthor = author;
    }

    private readonly Puck.Commands.Principal m_reloadAuthor;

    internal MutationDrawHistory? DrawHistory { get; init; }
    internal byte[]? ReloadPatch { get; init; }
    internal WorldDrawPositions? ReloadDrawPositions { get; init; }
    internal string Description => m_mutation is { } mutation ? WorldServer.Describe(mutation) : "world.reload";
    /// <summary>Gets who made the entry: the mutation's principal, or for a source edit the principal whose save
    /// submitted the reload.</summary>
    internal Puck.Commands.Principal Author => m_mutation?.Principal ?? m_reloadAuthor;
    /// <summary>Gets whether a program applied the mutation in a principal's name rather than a session submitting it: a
    /// scoped replay acting as the seat that recorded it, or an addon guest.</summary>
    internal bool Played { get; init; }
    /// <summary>Gets whether a principal authored the entry: a source edit, or a mutation a session submitted. An entry
    /// the World applied itself (a sweep's, a search's, a field's or a rule's document write, or a <see cref="Played"/>
    /// one) is document history that undo replays and never drops.</summary>
    internal bool Authored => (m_mutation is not { } mutation) || (!Played && (mutation.Principal != Puck.Commands.Principal.World));
    /// <summary>Gets whether its author undid the entry: it stays in the journal at its place, every replay skips it, and
    /// <c>world.redo</c> takes it back in place. Only an <see cref="Authored"/> entry is ever undone.</summary>
    internal bool Undone { get; init; }
    /// <summary>Gets whether the entry is an edit the author's sources do not hold: a mutation a session submitted and
    /// has not undone. A source edit came from a file that already holds it.</summary>
    internal bool Unsaved => (m_mutation is not null) && Authored && !Undone;

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
