namespace Puck.World;

/// <summary>Settles the rows an installed state section is born with: a clock on every cell whose effective behavior
/// is timed and whose record carries none, and the cells of every derived board.</summary>
/// <remarks>An install is a birth, so a rotation, an accumulation, or an ease runs from the tick the cell was
/// installed at rather than from the origin, and a derived board holds what its own tokens and codes rows recompute.
/// Every other field of every row stays the document's own, and settling an already-settled document returns the
/// instance it was given — which is what lets a caller settle before admission and install the exact document its
/// receipt names.</remarks>
public static class WorldStateSettlement {
    /// <summary>Returns the time an unstepped world's arena is born at.</summary>
    /// <param name="definition">The world document.</param>
    /// <returns>The boot time.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static ArenaTime BootTime(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return new ArenaTime(
            Dynamics: definition.Dynamics,
            EngineTick: 0UL,
            Tick: 0UL,
            TicksPerSecond: definition.SimulationRateHz
        );
    }
    /// <summary>Settles a document against an arena seeded from it at <paramref name="time"/>.</summary>
    /// <param name="definition">The document to settle.</param>
    /// <param name="time">The tick pair and dynamics the seed is born at.</param>
    /// <returns>The settled document, or the same instance when nothing settled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldDefinition Settle(WorldDefinition definition, in ArenaTime time) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (definition.State.Count == 0) {
            return definition;
        }
        if (!StateArena.TryCreate(
            arena: out var seeded,
            catalog: definition.StateCatalog,
            options: WorldSlotLanes.Options(definition: definition),
            reason: out _,
            section: definition.StateRaw,
            time: in time
        )) {
            return definition;
        }

        return SettleFrom(
            definition: definition,
            seeded: seeded
        );
    }
    /// <summary>Settles a document ahead of the one admission that will validate it.</summary>
    /// <param name="definition">The drawn document a loader is about to admit.</param>
    /// <returns>The settled document, or the same instance when nothing settled or the section cannot be held.</returns>
    /// <remarks>This runs over a document admission has not yet proved, so a section no arena can hold is handed on
    /// unchanged and the admission names the refusal in its own words.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldDefinition SettleBeforeAdmission(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        try {
            return Settle(
                definition: definition,
                time: BootTime(definition: definition)
            );
        } catch (Exception) {
            return definition;
        }
    }
    /// <summary>Settles a document against an arena a caller already seeded from it.</summary>
    /// <param name="definition">The document to settle.</param>
    /// <param name="seeded">The arena seeded from that document's own state section.</param>
    /// <returns>The settled document, or the same instance when nothing settled.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static WorldDefinition SettleFrom(WorldDefinition definition, StateArena seeded) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: seeded);

        var rows = definition.AuthoredState;
        var exported = seeded.ToRows();
        List<WorldStateRow>? settled = null;

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];

            if (StateRows.FindStateRow(
                rows: exported,
                name: row.Name.Value
            ) is not { } born) {
                continue;
            }
            // A derived board's cells are the arena's own recompute, never the document's — the board is never
            // authored, only derived. A board the recompute agrees with is left as the very object the caller
            // handed over, so a settlement that changed no board keeps the definition it was given and the
            // compilation receipt that names it.
            if (row.Inverse is not null) {
                if (SameBoardCells(
                    left: row.Cells,
                    right: born.Cells
                )) {
                    continue;
                }

                settled ??= new List<WorldStateRow>(collection: rows);
                settled[index] = (row with { Cells = born.Cells });

                continue;
            }
            if (row.Cells is not { Count: > 0 } cells) {
                continue;
            }

            List<StateCell>? bornCells = null;

            for (var cell = 0; (cell < cells.Count); cell++) {
                if (
                    (cells[cell].Clock is not null) ||
                    (StateRows.FindCell(
                    cells: born.Cells,
                    key: cells[cell].Key
                )?.Clock is not { } clock)
                ) {
                    continue;
                }

                bornCells ??= new List<StateCell>(collection: cells);
                bornCells[cell] = (cells[cell] with { Clock = clock });
            }

            if (bornCells is null) {
                continue;
            }

            settled ??= new List<WorldStateRow>(collection: rows);
            settled[index] = (row with { Cells = bornCells });
        }

        return ((settled is null)
            ? definition
            : definition.WithWorldState(rows: settled)
        );
    }

    // A board is a key and a value per occupied cell; nothing else about its cells is derived, so nothing else
    // decides whether the recompute moved it.
    private static bool SameBoardCells(IReadOnlyList<StateCell>? left, IReadOnlyList<StateCell>? right) {
        var authored = (left ?? []);
        var derived = (right ?? []);

        if (authored.Count != derived.Count) {
            return false;
        }

        for (var index = 0; (index < authored.Count); index++) {
            if (
                (authored[index].Key != derived[index].Key) ||
                (authored[index].Value != derived[index].Value)
            ) {
                return false;
            }
        }

        return true;
    }
}
