using Puck.World.Protocol;


namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Composes Generate as a PURE function of (candidate document, instance identity): the site's source resolves
    // from the document, WorldGeneratorEngine SEEKS the stream to the position the site's own DrawCursor records, and
    // BOTH the drawn value and the advanced cursor/decks land in the SAME candidate. Nothing lives outside the
    // document, which is what makes world.undo rewind a draw bit-identically with no bookkeeping to reconcile. The
    // sampling itself lives in Puck.World.Schema because the BOOT resolver — which runs before this server exists —
    // must reach the identical code.
    private static bool TryComposeGenerate(WorldDefinition current, WorldMutation.Generate mutation, string instanceIdentity, out WorldDefinition candidate, out string reason) {
        candidate = current;

        if (WorldDefinitionRows.FindStateRow(
            rows: current.State,
            name: mutation.Row
        ) is not { } siteRow) {
            reason = $"no state row named '{mutation.Row}'";

            return false;
        }

        // A lattice row painted by a draw fill: 'generate' advances the row's pass — one whole-field run of its
        // stream — in the document; the apply side then repaints the field at the new cursor/decks. The pass at the
        // CURRENT cursor is fired here only for its tail (the decks it ends with), never for its values.
        if (siteRow.Lattice is { } latticeTrait) {
            if (WorldLatticeFill.FindDraw(trait: latticeTrait) is not { } fill) {
                reason = $"state row '{mutation.Row}' is a lattice row with no draw paint — 'generate' redraws a draw site or a lattice row painted by a draw fill";

                return false;
            }

            if (!WorldGeneratorEngine.TryResolveSource(
                generators: current.Generators,
                draw: new WorldDraw(Source: fill.Source, Generator: fill.Generator),
                generator: out var fillSource,
                reason: out var fillResolveReason
            )) {
                reason = $"state row '{mutation.Row}' draw fill {fillResolveReason}";

                return false;
            }

            var shape = current.Fields!.Lattice;
            var cellCount = ((shape.Width * shape.Layers) * shape.Depth);
            var fillSite = WorldDrawSites.StateRow(rowName: siteRow.Name);

            if (!WorldGeneratorEngine.TryAdvanceBatch(
                generator: fillSource,
                targetKind: CellKind.Fixed,
                seedState: WorldGeneratorEngine.ComputeSeedState(
                    worldSeed: (current.Generation?.WorldSeed ?? 0UL),
                    instanceIdentity: instanceIdentity,
                    site: fillSite
                ),
                stream: WorldGeneratorEngine.ComputeStreamId(site: fillSite),
                cursor: siteRow.DrawCursor,
                decks: siteRow.DrawDecks,
                sampleCount: cellCount,
                decksAfter: out var decksAfter,
                reason: out var fillFireReason
            )) {
                reason = $"state row '{mutation.Row}' draw fill {fillFireReason}";

                return false;
            }

            candidate = current.WithWorldState(rows: Upsert(
                list: current.State,
                item: (siteRow with { DrawCursor = (siteRow.DrawCursor + cellCount), DrawDecks = WorldGeneratorEngine.DecksAfter(generator: fillSource, fired: decksAfter, previous: siteRow.DrawDecks) }),
                keyOf: static (WorldStateRow row) => row.Name
            ));
            reason = string.Empty;

            return true;
        }

        if (siteRow.Draw is not { } draw) {
            reason = $"state row '{mutation.Row}' declares no draw — 'generate' redraws a draw site";

            return false;
        }

        if (draw.Timing == WorldDrawTiming.Boot) {
            reason = $"state row '{mutation.Row}' declares timing=boot — it draws once at first fill and is never redrawn";

            return false;
        }

        if (siteRow.IsKeyed) {
            if (!WorldDrawBootResolver.TryFillKeyedSite(
                definition: current,
                worldSeed: (current.Generation?.WorldSeed ?? 0UL),
                instanceIdentity: instanceIdentity,
                row: siteRow,
                keys: mutation.Keys,
                filled: out var filled,
                reason: out var keyedReason
            )) {
                reason = $"state row '{mutation.Row}' {keyedReason}";

                return false;
            }

            candidate = current.WithWorldState(rows: Upsert(list: current.State, item: filled, keyOf: static (WorldStateRow row) => row.Name));
            reason = string.Empty;

            return true;
        }

        if (mutation.Keys is not null) {
            reason = $"state row '{mutation.Row}' is a slot site — keys select cells of a keyed site alone";

            return false;
        }

        if (!WorldGeneratorEngine.TryResolveSource(
            generators: current.Generators,
            draw: draw,
            generator: out var generator,
            reason: out var resolveReason
        )) {
            reason = $"state row '{mutation.Row}' {resolveReason}";

            return false;
        }

        var site = WorldDrawSites.StateRow(rowName: siteRow.Name);

        if (!WorldGeneratorEngine.TryFire(
            generator: generator,
            targetKind: siteRow.Kind,
            seedState: WorldGeneratorEngine.ComputeSeedState(
                worldSeed: (current.Generation?.WorldSeed ?? 0UL),
                instanceIdentity: instanceIdentity,
                site: site
            ),
            stream: WorldGeneratorEngine.ComputeStreamId(site: site),
            cursor: siteRow.DrawCursor,
            decks: siteRow.DrawDecks,
            result: out var fired,
            secret: draw.Secret,
            reason: out var fireReason
        )) {
            reason = $"state row '{mutation.Row}' {fireReason}";

            return false;
        }

        if (
            (fired.Text is { } emission) &&
            (emission.Length > WorldStateCapacity.MaxTextValueLength)
        ) {
            reason = $"state row '{mutation.Row}' emission length {emission.Length} exceeds the {WorldStateCapacity.MaxTextValueLength}-unit text bound";

            return false;
        }

        var cell = ((fired.Text is { } text)
            ? new WorldStateCell(
                Key: WorldStateRow.SlotKey,
                Text: text
            )
            : new WorldStateCell(
                Key: WorldStateRow.SlotKey,
                // A numeric draw is already in the site's own encoding — raw FixedQ4816 bits on a fixed row — the
                // contract the source's range/outcome values, the validator's domain narrowing and a lattice fill all
                // share.
                Value: fired.Numeric!.Value
            )
        );
        var state = Upsert(
            list: current.State,
            item: (siteRow with { Cells = [cell], DrawCursor = (siteRow.DrawCursor + fired.Samples), DrawDecks = WorldGeneratorEngine.DecksAfter(generator: generator, fired: fired.Decks, previous: siteRow.DrawDecks) }),
            keyOf: static (WorldStateRow row) => row.Name
        );

        candidate = current.WithWorldState(rows: state);
        reason = string.Empty;

        return true;
    }
}
