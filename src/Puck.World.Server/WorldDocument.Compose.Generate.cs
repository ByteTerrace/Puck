using Puck.World.Protocol;


namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // Composes Generate as a PURE function of (candidate document, instance identity): the site's source resolves
    // from the document, GeneratorEngine SEEKS the stream to the position the site's own DrawCursor records, and
    // BOTH the drawn value and the advanced cursor/masks land in the SAME candidate. Nothing lives outside the
    // document, which is what makes world.undo rewind a draw bit-identically with no bookkeeping to reconcile. The
    // sampling itself lives in Puck.World.Schema because the BOOT resolver — which runs before this server exists —
    // must reach the identical code.
    private static bool TryComposeGenerate(WorldDefinition current, WorldMutation.Generate mutation, string instanceIdentity, PrivateDrawAuthority sampler, out WorldDefinition candidate, out string reason) {
        candidate = current;

        if (WorldDefinitionRows.FindStateRow(
            rows: current.State,
            name: mutation.Row
        ) is not { } siteRow) {
            reason = $"no state row named '{mutation.Row}'";

            return false;
        }

        // A lattice row painted by a draw fill: 'generate' advances the row's pass — one whole-field run of its
        // stream — in the document; the apply side then repaints the field at the new cursor/masks. The pass at the
        // CURRENT cursor is fired here only for its tail (the masks it ends with), never for its values.
        if (siteRow.Field is { } latticeTrait) {
            if (WorldLatticeFill.FindDraw(trait: latticeTrait) is null) {
                reason = $"state row '{mutation.Row}' is a lattice row with no draw paint — 'generate' redraws a draw site or a lattice row painted by a draw fill";

                return false;
            }

            if (!WorldDrawBootResolver.TryAdvanceLatticePass(
                definition: current,
                instanceIdentity: instanceIdentity,
                row: siteRow,
                cursor: siteRow.DrawCursor,
                masks: siteRow.DrawnMasks,
                advances: out var advances,
                masksAfter: out var advancedMasks,
                reason: out reason
            )) {
                return false;
            }

            // The apply side paints the pass at the advanced position, so that pass is drawn here too: a draw the
            // source refuses (an exhausted bounded draw, a drawn-out mask) refuses the mutation rather than the repaint.
            if (!WorldDrawBootResolver.TryAdvanceLatticePass(
                definition: current,
                instanceIdentity: instanceIdentity,
                row: siteRow,
                cursor: (siteRow.DrawCursor + advances),
                masks: advancedMasks,
                advances: out _,
                masksAfter: out _,
                reason: out reason
            )) {
                return false;
            }

            candidate = current.WithWorldState(rows: Upsert(
                list: current.AuthoredState,
                item: (siteRow with {
                    DrawCursor = (siteRow.DrawCursor + advances),
                    DrawnMasks = advancedMasks,
                }),
                keys: WorldRowKeys.StateRows
            ));
            reason = string.Empty;

            return true;
        }

        if (siteRow.Draw is not { } draw) {
            reason = $"state row '{mutation.Row}' declares no draw — 'generate' redraws a draw site";

            return false;
        }

        if (draw.Timing == DrawTiming.Boot) {
            reason = $"state row '{mutation.Row}' declares timing=boot — it draws once at first fill and is never redrawn";

            return false;
        }

        if (!WorldDrawBootResolver.TryFillSite(current, instanceIdentity, siteRow, mutation.Keys, sampler, out var filled, out reason)) { return false; }
        var state = Upsert(list: current.AuthoredState, item: filled, keys: WorldRowKeys.StateRows);
        candidate = current.WithWorldState(rows: state);
        reason = string.Empty;

        return true;
    }
}
