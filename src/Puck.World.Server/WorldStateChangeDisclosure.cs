using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Checks each change delivery against its recipient's current arena audience, reusing delivery scratch.</summary>
public sealed class WorldStateChangeDisclosure {
    private FieldCellDelta[] m_result = [];
    private bool[] m_changed = [];
    // The cells last sent for each row that can withhold from this recipient; null until sent, or after Forget.
    private FieldCellDelta[]?[] m_sent = [];
    private StateArena? m_sentArena;
    private int m_count;

    private void Add(FieldCellDelta delta) {
        if (m_count == m_result.Length) { Array.Resize(array: ref m_result, newSize: Math.Max(val1: 16, val2: (m_count * 2))); }
        m_result[m_count++] = delta;
    }

    /// <summary>Composes disclosed row replacements from changed positions. No audience decision is retained.
    /// The returned memory is borrowed until the next call, like the snapshot it joins.</summary>
    public ReadOnlyMemory<FieldCellDelta> Filter(WorldServer server, Principal? recipient, ReadOnlyMemory<FieldCellDelta> changes, bool rows) {
        // Images can hold text and vectors; release the previous delivery even when this one is empty.
        Array.Clear(array: m_result, index: 0, length: m_count);
        m_count = 0;
        if (changes.IsEmpty) { return ReadOnlyMemory<FieldCellDelta>.Empty; }
        var arena = server.Arena;

        if (m_changed.Length != arena.Rows.Count) { m_changed = new bool[arena.Rows.Count]; } else { Array.Clear(array: m_changed); }
        var withheld = false;

        foreach (var delta in changes.Span) {
            if (delta.Row < 0) {
                var fields = server.Population.TryFieldRegion(name: delta.Region)?.Lattice.Input.Fields;

                if ((fields is null) || (((uint)delta.Field) >= ((uint)fields.Count)) || (delta.Cell < 0) || (fields[delta.Field].Name != delta.FieldName)) { withheld = true; continue; }
                if (arena.Catalog.TryResolve(StateLane.Document, fields[delta.Field].Name, out var row) &&
                    !WorldStateDisclosure.CanRead(server.Configuration, arena, arena.Rows[row.Ordinal],
                        CellName.Parse(candidate: IndexKeyCache.Get(index: delta.Cell)), recipient)) { withheld = true; continue; }
                Add(delta: delta);
            } else { m_changed[delta.Row] = true; }
        }
        if (!rows) { return m_result.AsMemory(length: m_count, start: 0); }
        var policiesMoved = false;

        for (var ordinal = 0; (ordinal < arena.Rows.Count); ordinal++) {
            policiesMoved |= (m_changed[ordinal] && (arena.Rows[ordinal].EffectiveDomain is StateDomain.KeysOf { Ordered: true }));
        }
        for (var ordinal = 0; (!policiesMoved && (ordinal < arena.Rows.Count)); ordinal++) {
            policiesMoved |= ReadersMoved(arena: arena, policy: arena.Rows[ordinal].Visibility);
            if (!arena.HasCellVisibility(rowOrdinal: ordinal)) { continue; }
            var cursor = 0;

            while (!policiesMoved && arena.TryNextCell(cursor: ref cursor, key: out var key, rowOrdinal: ordinal)) {
                policiesMoved |= ReadersMoved(arena: arena, policy: arena.Visibility(key: key, rowOrdinal: ordinal));
            }
        }
        if (!policiesMoved && !withheld && DisclosesWhole(arena: arena, changes: changes.Span, recipient: recipient)) {
            // The snapshot's own replacements are already what composing them for this recipient would produce.
            Array.Clear(array: m_result, index: 0, length: m_count);
            m_count = 0;
            return changes;
        }
        if (!ReferenceEquals(objA: m_sentArena, objB: arena) || (m_sent.Length != arena.Rows.Count)) {
            m_sent = new FieldCellDelta[]?[arena.Rows.Count];
            m_sentArena = arena;
        }
        for (var ordinal = 0; (ordinal < arena.Rows.Count); ordinal++) {
            if (!policiesMoved && !m_changed[ordinal]) { continue; }
            var header = m_count;

            Add(delta: new FieldCellDelta(Cell: -1, Field: 0, Raw: 0, Row: ordinal));
            var cursor = 0; var position = 0;

            while (arena.TryNextCell(cursor: ref cursor, key: out var key, rowOrdinal: ordinal)) {
                if (!WorldStateDisclosure.CanRead(server.Configuration, arena, arena.Rows[ordinal], arena.Keys[key], recipient)) { continue; }
                Add(delta: new FieldCellDelta(Cell: position++, Field: 0, Raw: 0, Row: ordinal, Image: arena.ReadCellImage(key: key, rowOrdinal: ordinal)));
            }
            m_result[header] = m_result[header] with { Raw = position };
            if (WorldStateDisclosure.ReadsWholeRow(arena: arena, recipient: recipient, rowOrdinal: ordinal)) { continue; }
            // A row that can withhold is replaced only when what the recipient reads of it moved: a replacement on every
            // tick its hidden cells change would show when they do.
            var composed = m_result.AsSpan(start: (header + 1), length: position);

            if ((m_sent[ordinal] is { } sent) && composed.SequenceEqual(other: sent)) {
                Array.Clear(array: m_result, index: header, length: (m_count - header));
                m_count = header;
                continue;
            }
            m_sent[ordinal] = composed.ToArray();
        }
        return m_result.AsMemory(length: m_count, start: 0);
    }

    /// <summary>Forgets every restricted row replacement this recipient was sent: the recipient's rows were just
    /// seeded whole by a definition or state delivery, so the next change of each row is sent again.</summary>
    public void Forget() => Array.Clear(array: m_sent);

    // Whether the snapshot's row replacements are exactly the recipient's: every replaced row reads whole for it (no
    // cell or zone policy can withhold a cell, and its own policy admits the recipient), and each replacement's
    // header counts the cells that follow it, as a composed header counts the cells it disclosed.
    private static bool DisclosesWhole(StateArena arena, ReadOnlySpan<FieldCellDelta> changes, Principal? recipient) {
        var index = 0;

        while ((index < changes.Length) && (changes[index].Row < 0)) { index++; }
        while (index < changes.Length) {
            var header = changes[index];

            if ((header.Cell != -1) || !WorldStateDisclosure.ReadsWholeRow(arena: arena, recipient: recipient, rowOrdinal: header.Row)) {
                return false;
            }
            var cells = 0;

            index++;
            while ((index < changes.Length) && (changes[index].Row == header.Row) && (changes[index].Cell >= 0)) { cells++; index++; }
            if (cells != header.Raw) { return false; }
        }
        return true;
    }

    private bool ReadersMoved(StateArena arena, StateVisibility? policy) => ((policy?.ReadersFrom is { } readers) &&
        arena.Catalog.TryResolve(handle: out var row, lane: StateLane.Document, name: readers) && m_changed[row.Ordinal]);
}
