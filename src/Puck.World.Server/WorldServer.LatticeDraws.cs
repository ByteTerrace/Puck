using System.Buffers;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Paints every lattice row's draw fill at the pass its cursor/decks name — the whole-field counterpart of the
    // boot resolver's first fill for a state-row site. Runs once at construction (the lattice is allocated by then
    // and never reallocated for the server's life), and again for one row after a Generate on it advances that
    // row's pass. Reactions then evolve the drawn cells like any other paint.
    private void PaintLatticeDraws(WorldDefinition definition) {
        foreach (var row in (definition.State ?? [])) {
            if (WorldLatticeFill.FindDraw(trait: row.Lattice) is not null) {
                PaintLatticeDraw(
                    definition: definition,
                    row: row
                );
            }
        }
    }
    private void PaintLatticeDraw(WorldDefinition definition, WorldStateRow row) {
        if (
            (m_population.Fields is not { } lattice) ||
            (WorldLatticeFill.FindDraw(trait: row.Lattice) is not { } fill) ||
            !lattice.TryFieldIndex(
                field: out var field,
                name: row.Name.Value
            )
        ) {
            return;
        }

        if (!WorldGeneratorEngine.TryResolveSource(
            generators: definition.Generators,
            draw: new WorldDraw(Source: fill.Source, Generator: fill.Generator),
            generator: out var generator,
            reason: out var resolveReason
        )) {
            throw new InvalidOperationException(message: $"state row '{row.Name}' draw fill {resolveReason} (a validated document must still resolve when it paints).");
        }

        var site = WorldDrawSites.StateRow(rowName: row.Name);
        var worldSeed = (definition.Generation?.WorldSeed ?? 0UL);
        var values = ArrayPool<long>.Shared.Rent(minimumLength: lattice.CellCount);

        try {
            var pass = values.AsSpan(start: 0, length: lattice.CellCount);

            if (!WorldGeneratorEngine.TryFireBatch(
                generator: generator,
                targetKind: CellKind.Fixed,
                seedState: WorldGeneratorEngine.ComputeSeedState(
                    worldSeed: worldSeed,
                    instanceIdentity: InstanceIdentity,
                    site: site
                ),
                stream: WorldGeneratorEngine.ComputeStreamId(site: site),
                cursor: row.DrawCursor,
                decks: row.DrawDecks,
                values: pass,
                decksAfter: out _,
                reason: out var fireReason
            )) {
                throw new InvalidOperationException(message: $"state row '{row.Name}' draw fill {fireReason} (a validated document must still draw when it paints).");
            }

            lattice.FillFromDraw(
                field: field,
                raw: pass,
                worldSeed: worldSeed
            );
        } finally {
            ArrayPool<long>.Shared.Return(array: values);
        }
    }
    // The apply-side half of a Generate on a lattice row: the compose advanced the row's pass, so the field now
    // repaints at it. A Generate on an ordinary draw site wrote its cell in the document and paints nothing.
    private void RepaintLatticeDrawAfterGenerate(WorldDefinition definition, string rowName) {
        if (
            (WorldDefinitionRows.FindStateRow(
                rows: definition.State,
                name: rowName
            ) is { } row) &&
            (row.Lattice is not null)
        ) {
            PaintLatticeDraw(
                definition: definition,
                row: row
            );
        }
    }
    // Undo preserves the live lattice allocation and reaction state. Repaint only rows whose persisted draw
    // position actually moved, so rewinding an unrelated mutation cannot erase simulation-evolved cells.
    private void RepaintChangedLatticeDraws(WorldDefinition previous, WorldDefinition current) {
        foreach (var row in (current.State ?? [])) {
            if (
                (WorldLatticeFill.FindDraw(trait: row.Lattice) is not null) &&
                (WorldDefinitionRows.FindStateRow(rows: previous.State, name: row.Name.Value) is { } oldRow) &&
                (
                    (oldRow.DrawCursor != row.DrawCursor) ||
                    !(oldRow.DrawDecks ?? []).SequenceEqual(second: (row.DrawDecks ?? []))
                )
            ) {
                PaintLatticeDraw(
                    definition: current,
                    row: row
                );
            }
        }
    }
}
