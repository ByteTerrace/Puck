using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // Draw values and continuation share the caller's transaction. Native field paint has its own snapshot owner.
    private bool TryGenerateState(WorldMutation.Generate mutation, in ArenaTime time, out string reason) {
        var arena = Host.Arena;
        reason = string.Empty;
        if (!arena.Catalog.TryResolve(StateLane.Document, mutation.Row, out var row) ||
            arena.Rows[row.Ordinal].Draw is not { Timing: not DrawTiming.Boot } draw) {
            reason = "generate requires a redrawable draw site"; return false;
        }
        if (!GeneratorEngine.TryResolveSource(m_definition.Generators, draw, out var generator, out reason)) { return false; }
        var site = WorldDrawSites.StateRow(arena.Rows[row.Ordinal].Name);
        var seed = new DrawSeed(GeneratorEngine.ComputeSeedState(m_definition.Generation?.WorldSeed ?? 0UL, Host.InstanceIdentity, site), GeneratorEngine.ComputeStreamId(site));
        if (!arena.Rows[row.Ordinal].IsKeyed) {
            if (mutation.Keys is not null) { reason = "keys select cells of a keyed draw site alone"; return false; }
            if (!ArenaDraws.TryFire(arena, row.Ordinal, generator, seed, out var fired, out reason, draw.Secret, draw.Skip)) { return false; }
            var key = arena.Keys.Intern(StateRow.SlotKey);
            return fired.Text is { } text ? arena.TryWriteText(row.Ordinal, key, text, out reason)
                : arena.TryWriteLive(row.Ordinal, key, fired.Numeric!.Value, StateWriteKind.Set, time, out reason);
        }
        var selected = new List<CellKey>();
        var cursor = 0;
        while (arena.TryNextCell(row.Ordinal, ref cursor, out var key)) {
            if (mutation.Keys is null || mutation.Keys.Contains(arena.Keys[key].Value, StringComparer.Ordinal)) { selected.Add(key); }
        }
        if (mutation.Keys is { } keys && (selected.Count != keys.Count || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)) {
            reason = "generate names an absent or repeated cell"; return false;
        }
        if (selected.Count == 0) { return true; }
        var before = arena.DrawCursor(row.Ordinal);
        var masks = ArenaDraws.ReadMasks(arena, row.Ordinal);
        var values = new long[selected.Count];
        if (!GeneratorEngine.TryFireBatch(generator, arena.Layout[row.Ordinal].Kind, seed.State, seed.Stream, before, masks,
            values, out var after, out reason, draw.Skip)) { return false; }
        if (!ArenaDraws.TryWrite(arena, row.Ordinal, checked(before + selected.Count), GeneratorEngine.MasksAfter(generator, after, masks), out reason)) { return false; }
        for (var i = 0; i < selected.Count; i++) {
            if (!arena.TryWriteLive(row.Ordinal, selected[i], values[i], StateWriteKind.Set, time, out reason)) { return false; }
        }
        return true;
    }
}
