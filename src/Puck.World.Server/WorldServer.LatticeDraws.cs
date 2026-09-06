using System.Buffers;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Paints every lattice row's draw fill at the pass its cursor/masks name — the whole-field counterpart of the
    // boot resolver's first fill for a state-row site. Runs once at construction (the lattice is allocated by then
    // and never reallocated for the server's life), and again for one row after a Generate on it advances that
    // row's pass. Reactions then evolve the drawn cells like any other paint.
    private void PaintLatticeDraws(WorldDefinition definition) {
        foreach (var row in (definition.State ?? [])) {
            if (WorldLatticeFill.FindDraw(trait: row.Field) is not null) {
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
            (WorldLatticeFill.FindDraw(trait: row.Field) is not { } fill) ||
            !lattice.TryFieldIndex(
                field: out var field,
                name: row.Name.Value
            )
        ) {
            return;
        }

        if (!GeneratorEngine.TryResolveSource(
            generators: definition.Generators,
            draw: new Draw(Source: fill.Source, Generator: fill.Generator),
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

            if (!GeneratorEngine.TryFireBatch(
                generator: generator,
                targetKind: CellKind.Fixed,
                seedState: GeneratorEngine.ComputeSeedState(
                    documentSeed: worldSeed,
                    instanceIdentity: InstanceIdentity,
                    site: site
                ),
                stream: GeneratorEngine.ComputeStreamId(site: site),
                cursor: row.DrawCursor,
                masks: row.DrawnMasks,
                values: pass,
                masksAfter: out _,
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
    // Every apply and every undo preserves the live lattice allocation and reaction state. Repaint only rows whose
    // persisted draw position or draw fill actually moved, so an unrelated mutation cannot erase evolved cells.
    // Pairs a current row with its previous self by array position (a name compare, not a scan) whenever both row
    // lists carry the same count — the common case, since almost every mutation kind that reaches here changes a
    // row's own cursor/masks/field rather than adding, removing, or reordering rows — and falls back to a name-keyed
    // map, built once and reused for the rest of this call, only for a row a position pairing did not resolve. Either
    // path is O(rows) total; a per-row WorldDefinitionRows.FindStateRow scan against the whole previous list would be
    // O(rows squared).
    private void RepaintChangedLatticeDraws(WorldDefinition previous, WorldDefinition current) {
        if (ReferenceEquals(objA: previous.State, objB: current.State)) {
            return;
        }

        var previousRows = (previous.State ?? []);
        var currentRows = (current.State ?? []);
        var sameCount = (previousRows.Count == currentRows.Count);
        Dictionary<string, WorldStateRow>? previousByName = null;

        for (var index = 0; (index < currentRows.Count); index++) {
            var row = currentRows[index];

            if (WorldLatticeFill.FindDraw(trait: row.Field) is not { } fill) {
                continue;
            }

            WorldStateRow? oldRow = null;

            if (
                sameCount &&
                string.Equals(a: previousRows[index].Name.Value, b: row.Name.Value, comparisonType: StringComparison.Ordinal)
            ) {
                oldRow = previousRows[index];
            } else {
                previousByName ??= BuildRowsByName(rows: previousRows);
                previousByName.TryGetValue(key: row.Name.Value, value: out oldRow);
            }

            if (
                (oldRow is { } found) &&
                (
                    (found.DrawCursor != row.DrawCursor) ||
                    !SameMasks(left: found.DrawnMasks, right: row.DrawnMasks) ||
                    !Equals(objA: WorldLatticeFill.FindDraw(trait: found.Field), objB: fill)
                )
            ) {
                PaintLatticeDraw(
                    definition: current,
                    row: row
                );
            }
        }
    }
    private static Dictionary<string, WorldStateRow> BuildRowsByName(IReadOnlyList<WorldStateRow> rows) {
        var byName = new Dictionary<string, WorldStateRow>(capacity: rows.Count, comparer: StringComparer.Ordinal);

        for (var index = 0; (index < rows.Count); index++) {
            byName[rows[index].Name.Value] = rows[index];
        }

        return byName;
    }
    private static bool SameMasks(IReadOnlyList<ClosedBitset256>? left, IReadOnlyList<ClosedBitset256>? right) {
        var leftCount = (left?.Count ?? 0);

        if (leftCount != (right?.Count ?? 0)) {
            return false;
        }

        for (var index = 0; (index < leftCount); index++) {
            if (left![index] != right![index]) {
                return false;
            }
        }

        return true;
    }
}
