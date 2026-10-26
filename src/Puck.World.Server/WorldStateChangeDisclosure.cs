using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Checks each change delivery against its recipient's current arena audience.</summary>
public static class WorldStateChangeDisclosure {
    /// <summary>Composes disclosed row replacements from changed positions. No audience decision is retained.</summary>
    public static ReadOnlyMemory<FieldCellDelta> Filter(WorldServer server, Principal? recipient, ReadOnlyMemory<FieldCellDelta> changes, bool rows) {
        if (changes.IsEmpty) { return ReadOnlyMemory<FieldCellDelta>.Empty; }
        var arena = server.Arena;
        var result = new List<FieldCellDelta>();
        var changed = new HashSet<int>();
        foreach (var delta in changes.Span) {
            if (delta.Row < 0) { result.Add(delta); }
            else { changed.Add(delta.Row); }
        }
        if (!rows) { return result.ToArray(); }
        var policiesMoved = false;
        var names = changed.Select(ordinal => arena.Rows[ordinal].Name.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var ordinal in changed) {
            policiesMoved |= arena.Rows[ordinal].EffectiveDomain is StateDomain.KeysOf { Ordered: true };
        }
        for (var ordinal = 0; !policiesMoved && ordinal < arena.Rows.Count; ordinal++) {
            policiesMoved |= arena.Rows[ordinal].Visibility?.ReadersFrom is { } from && names.Contains(from);
            var cursor = 0;
            while (!policiesMoved && arena.TryNextCell(ordinal, ref cursor, out var key)) {
                policiesMoved |= arena.Visibility(ordinal, key)?.ReadersFrom is { } readers && names.Contains(readers);
            }
        }
        for (var ordinal = 0; ordinal < arena.Rows.Count; ordinal++) {
            if (!policiesMoved && !changed.Contains(ordinal)) { continue; }
            var header = result.Count;
            result.Add(new FieldCellDelta(Cell: -1, Field: 0, Raw: 0, Row: ordinal));
            var cursor = 0; var position = 0;
            while (arena.TryNextCell(ordinal, ref cursor, out var key)) {
                if (!WorldStateDisclosure.CanRead(server.Configuration, arena, arena.Rows[ordinal], arena.Keys[key], recipient)) { continue; }
                result.Add(new FieldCellDelta(Cell: position++, Field: 0, Raw: 0, Row: ordinal, Image: arena.ReadCellImage(ordinal, key)));
            }
            result[header] = result[header] with { Raw = position };
        }
        return result.ToArray();
    }
}
