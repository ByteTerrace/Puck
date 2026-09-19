namespace Puck.World.Server;

/// <summary>
/// The verdict rows of one installed document and their witnesses, addressed by the catalog ordinal the arena
/// writes through, and the stamp the effect door leaves on each verdict.
/// </summary>
/// <remarks>
/// <para>Built once per arena layout and shared by every <see cref="WorldArenaHost"/> over that layout, so the stamp
/// is the same fact at the live tick door and at a submitted operation's compose arena rather than two copies of it.
/// <see cref="From"/> returns <see langword="null"/> for a document declaring no verdict row, which is every world
/// but a test world.</para>
/// <para>The stamp is simulation state: it is an ordinary cell of the row, so the arena's export carries it into the
/// document, the state hash folds it, and a checkpoint restores it.</para>
/// </remarks>
public sealed class WorldVerdictStamp {
    // Indexed by catalog ordinal. A verdict row holds its own ordinal; a witness holds its verdict's, so a write to
    // either is judged and stamped on the one row that carries a status.
    private readonly int[] m_verdict;
    private readonly CellName[] m_status;

    private WorldVerdictStamp(CellName[] status, int[] verdict) {
        m_status = status;
        m_verdict = verdict;
    }

    /// <summary>Returns the verdict rows of an installed document, or <see langword="null"/> when it declares
    /// none.</summary>
    /// <param name="definition">The installed document.</param>
    /// <param name="catalog">The catalog the arena's row ordinals address.</param>
    /// <returns>The map, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="catalog"/> is
    /// <see langword="null"/>.</exception>
    public static WorldVerdictStamp? From(WorldDefinition definition, StateCatalog catalog) {
        ArgumentNullException.ThrowIfNull(argument: catalog);
        ArgumentNullException.ThrowIfNull(argument: definition);

        CellName[]? status = null;
        int[]? verdicts = null;

        foreach (var row in definition.State) {
            if (
                (row is null) ||
                !row.IsRuleWritten ||
                (WorldDefinitionRows.FindStateRow(
                    name: (row.Witness?.Value ?? row.Name.Value),
                    rows: definition.State
                ) is not { Verdict: { } verdict } judged) ||
                !catalog.TryResolve(
                    handle: out var handle,
                    lane: StateLane.Document,
                    name: row.Name
                ) ||
                !catalog.TryResolve(
                    handle: out var judgedHandle,
                    lane: StateLane.Document,
                    name: judged.Name
                )
            ) {
                continue;
            }

            status ??= new CellName[catalog.Count];
            verdicts ??= new int[catalog.Count];

            if (((uint)handle.Ordinal) < ((uint)status.Length)) {
                status[handle.Ordinal] = verdict.Status;
                verdicts[handle.Ordinal] = judgedHandle.Ordinal;
            }
        }

        return (((status is null) || (verdicts is null))
            ? null
            : new WorldVerdictStamp(
                status: status,
                verdict: verdicts
            )
        );
    }

    /// <summary>Determines whether a catalog ordinal addresses a verdict row or a witness of one.</summary>
    /// <param name="rowOrdinal">The catalog ordinal a mutation names.</param>
    /// <param name="verdictOrdinal">The ordinal of the verdict row that judges the write: the row itself, or the
    /// verdict a witness names.</param>
    /// <param name="status">That verdict row's status cell key, on success.</param>
    /// <returns><see langword="true"/> when the ordinal addresses either.</returns>
    public bool TryResolve(int rowOrdinal, out int verdictOrdinal, out CellName status) {
        if (((uint)rowOrdinal) >= ((uint)m_status.Length)) {
            status = default;
            verdictOrdinal = -1;

            return false;
        }

        status = m_status[rowOrdinal];
        verdictOrdinal = m_verdict[rowOrdinal];

        return (status.Value is { Length: > 0 });
    }
    /// <summary>Determines whether a verdict row is settled: it fired a failing status on an earlier tick, so what a
    /// later firing would write is dropped.</summary>
    /// <param name="arena">The store the row lives in.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="status">The row's status cell key.</param>
    /// <param name="tick">The tick of the firing in flight.</param>
    /// <returns><see langword="true"/> when the row's status already reads <see cref="WorldVerdict.Fail"/> and the
    /// stamp names an earlier tick.</returns>
    /// <remarks>The stamp comparison is what keeps the freeze at the tick boundary: a firing that writes the status
    /// and then a value its gate saw is one firing, and both writes belong to the tick the stamp names.</remarks>
    public static bool IsSettled(StateArena arena, int rowOrdinal, CellName status, ulong tick) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        return (
            arena.TryRead(
            key: arena.Catalog.Keys.Intern(name: status),
            rowOrdinal: rowOrdinal,
            value: out var stored
        ) &&
            (stored.Kind == CellKind.Int) &&
            (stored.AsInt == WorldVerdict.Fail) &&
            arena.TryRead(
            key: arena.Catalog.Keys.Intern(name: WorldVerdict.FiredTickKey),
            rowOrdinal: rowOrdinal,
            value: out var stamp
        ) &&
            (stamp.Kind == CellKind.Int) &&
            (((ulong)stamp.AsInt) < tick)
        );
    }
    /// <summary>Stamps a verdict row with the tick of the firing that just wrote it.</summary>
    /// <param name="arena">The store the row lives in.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="tick">The tick of the firing.</param>
    /// <returns><see langword="true"/> when the stamp landed.</returns>
    public static bool Stamp(StateArena arena, int rowOrdinal, ulong tick) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        var key = arena.Catalog.Keys.Intern(name: WorldVerdict.FiredTickKey);

        return (arena.TryCellSlot(
            key: key,
            rowOrdinal: rowOrdinal,
            slot: out _
        )
            ? arena.TryWrite(
                key: key,
                operand: ((long)tick),
                reason: out _,
                rowOrdinal: rowOrdinal,
                write: StateWriteKind.Set
            )
            : arena.TryMint(
                key: out _,
                name: WorldVerdict.FiredTickKey,
                reason: out _,
                rowOrdinal: rowOrdinal,
                value: CellValue.Int(value: ((long)tick))
            )
        );
    }
}
