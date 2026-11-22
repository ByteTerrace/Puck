using System.Collections.Immutable;

namespace Puck.World.Server;

/// <summary>The authority half of a rule-fired <c>save</c>, captured where the rule fires: the session snapshot
/// (<see cref="WorldSessionCapture.Capture"/>) and the journal as it stood. The host folds its presentation levers into
/// <see cref="Snapshot"/> and writes the file when the server's outbox collects; a written save then compacts the
/// journal through the entries this capture saw (<see cref="CompactSaved"/>), so a save records the state at that
/// point of the tick, <c>world.undo</c> keeps the entries made after it, and a refused write compacts nothing.</summary>
public sealed class WorldSaveCapture {
    private readonly WorldServer m_server;
    private readonly WorldDefinition m_base;
    private readonly ImmutableList<WorldJournalEntry> m_journal;

    internal WorldSaveCapture(WorldServer server, ulong tick, WorldDefinition snapshot, WorldDefinition @base, ImmutableList<WorldJournalEntry> journal) {
        m_server = server;
        m_base = @base;
        m_journal = journal;
        Snapshot = snapshot;
        Tick = tick;
    }

    /// <summary>Gets the tick the save settled at.</summary>
    public ulong Tick { get; }
    /// <summary>Gets the authority half of the snapshot, with every moving state cell settled at <see cref="Tick"/>.</summary>
    public WorldDefinition Snapshot { get; }

    /// <summary>Compacts the journal through the entries it held when the rule fired: the live document as of the save
    /// becomes the base, and every later entry stays undoable. Nothing compacts when those entries are no longer the
    /// journal's first (an undo took one back since).</summary>
    /// <returns>Whether the journal compacted.</returns>
    public bool CompactSaved() => m_server.CompactThrough(@base: m_base, saved: m_journal);
}
