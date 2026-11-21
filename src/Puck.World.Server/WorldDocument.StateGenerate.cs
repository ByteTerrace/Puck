using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // Draw values and continuation share the caller's transaction. Native field paint has its own snapshot owner.
    private bool TryGenerateState(WorldMutation.Generate mutation, in ArenaTime time, out string reason) {
        var arena = Host.Arena;

        reason = string.Empty;
        if (!arena.Catalog.TryResolve(StateLane.Document, mutation.Row, out var row) ||
            (arena.Rows[row.Ordinal].Draw is not { Timing: not DrawTiming.Boot })) {
            reason = "generate requires a redrawable draw site"; return false;
        }
        var clock = time;
        var declaration = arena.Rows[row.Ordinal];
        if (!WorldDrawBootResolver.TryGenerate(m_definition, Host.InstanceIdentity, declaration, Host.DrawKeys, arena.DrawCursor(row.Ordinal),
            ArenaDraws.ReadMasks(arena, row.Ordinal), mutation.Keys, arena.Keys.Intern(StateRow.SlotKey), Cells(),
            key => arena.Keys[key].Value, Write, out var next, out var masks, out reason)) { return false; }
        return ArenaDraws.TryWrite(arena, row.Ordinal, next, masks, out reason);

        IEnumerable<CellKey> Cells() {
            var cursor = 0;
            while (arena.TryNextCell(row.Ordinal, ref cursor, out var key)) { yield return key; }
        }

        bool Write(CellKey key, CellValue value, out string refusal) =>
            value.Kind == CellKind.Text ? arena.TryWriteText(row.Ordinal, key, value.AsText!, out refusal)
            : arena.TryWriteLive(row.Ordinal, key, value.Raw, StateWriteKind.Set, clock, out refusal);
    }
}
