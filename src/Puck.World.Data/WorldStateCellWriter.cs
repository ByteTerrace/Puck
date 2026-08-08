using System.Globalization;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The shared PURE composition for one <see cref="WorldStateRow"/> cell write — upsert-or-append plus the row's own
/// <see cref="WorldStateRow.Evicts"/> overflow policy. Extracted so the running world's own ordered mutation
/// pipeline (<c>Server.WorldServer</c>'s <c>UpsertStateCell</c> compose arm) and any OWNED IDENTITY document write
/// outside that pipeline (<c>WorldIdentity.TryAppendEvictingText</c>, and through it the cross-document text
/// delivery door, <c>Server.WorldOwnedWorlds.Decide</c>) run the IDENTICAL rule and can never disagree about a
/// victim, a duplicate key, or a reserved-cell refusal — a second reading of "how a row absorbs one cell write" is
/// exactly the drift this repository's docs warn eviction logic against (see <see cref="WorldStateRow.Evicts"/>'s
/// own remarks: "the identical pure function... so a live apply and every world.undo re-composition run the
/// identical pure function... and can never disagree about the victim").
/// </summary>
public static class WorldStateCellWriter {
    /// <summary>Parses a human-authored wire token into a cell's raw-encoded operand, against a row <c>Kind</c>
    /// resolved from the CANDIDATE document at compose time — never at console submit time, where the row this token
    /// targets may not exist yet in the same batch (see <c>WorldMutation.UpsertStateCell.RawToken</c>'s remarks for
    /// why the interpretation cannot happen any earlier). Mirrors the console's former per-verb parse exactly: DECIMAL
    /// text for <see cref="CellKind.Fixed"/> (never raw <see cref="FixedQ4816"/> bits), <c>true</c>/<c>false</c> for
    /// <see cref="CellKind.Bool"/>, and a plain integer literal otherwise. Never called for <see cref="CellKind.Text"/>
    /// — a text write carries its operand through <see cref="WorldStateCell.Text"/> and never reaches this parser.</summary>
    /// <param name="kind">The destination row's declared kind (never <see cref="CellKind.Text"/>).</param>
    /// <param name="token">The raw wire token exactly as typed.</param>
    /// <param name="value">The parsed raw-encoded operand on success.</param>
    /// <param name="reason">Why the token was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the token parsed under <paramref name="kind"/>'s grammar.</returns>
    public static bool TryParseNumericToken(CellKind kind, string token, out long value, out string reason) {
        switch (kind) {
            case CellKind.Fixed:
                if (FixedQ4816.TryParse(s: token, provider: CultureInfo.InvariantCulture, result: out var fixedValue)) {
                    value = fixedValue.Value;
                    reason = string.Empty;

                    return true;
                }

                value = 0L;
                reason = $"'{token}' is not a decimal value (e.g. \"12.5\")";

                return false;
            case CellKind.Bool:
                if (bool.TryParse(value: token, result: out var boolValue)) {
                    value = (boolValue ? 1L : 0L);
                    reason = string.Empty;

                    return true;
                }

                value = 0L;
                reason = $"'{token}' is not 'true' or 'false'";

                return false;
            default:
                if (long.TryParse(s: token, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out value)) {
                    reason = string.Empty;

                    return true;
                }

                reason = $"'{token}' is not an integer";

                return false;
        }
    }

    /// <summary>Determines whether <paramref name="key"/> is ALREADY present in <paramref name="cells"/> — asked before an
    /// upsert, since the upsert's own replaced-vs-appended distinction is not itself observable to the caller. An
    /// eviction only ever fires for a write that minted a brand-new key, never one that re-wrote an existing one in
    /// place.</summary>
    /// <param name="cells">The row's current cells.</param>
    /// <param name="key">The key to look for.</param>
    /// <returns><see langword="true"/> when a cell already carries <paramref name="key"/>.</returns>
    public static bool ContainsKey(IReadOnlyList<WorldStateCell> cells, WorldCellName key) {
        foreach (var cell in cells) {
            if (cell.Key == key) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the row's own overflow policy, applied AFTER a write already appended-or-replaced a cell — the ONE
    /// eviction seam (see <see cref="WorldStateRow.Evicts"/>). A no-op (returns <paramref name="cells"/> unchanged,
    /// <paramref name="evictedKey"/> null) unless the row opts in, the write added a new key, AND the addition
    /// pushed the row past its declared capacity.</summary>
    /// <param name="row">The carrying row (for its <see cref="WorldStateRow.Evicts"/>/<see cref="WorldStateRow.Capacity"/>).</param>
    /// <param name="cells">The cells AFTER the triggering write already applied.</param>
    /// <param name="addedNewKey">Whether the triggering write minted a brand-new key rather than rewriting an existing one.</param>
    /// <param name="evictedKey">The evicted key, or <see langword="null"/> when nothing was evicted.</param>
    /// <returns>The post-eviction cell list.</returns>
    public static IReadOnlyList<WorldStateCell> ApplyEviction(WorldStateRow row, IReadOnlyList<WorldStateCell> cells, bool addedNewKey, out WorldCellName? evictedKey) {
        evictedKey = null;

        if (!row.Evicts || !addedNewKey || (row.Capacity is not { } capacity)) {
            return cells;
        }

        var effectiveCapacity = Math.Clamp(value: capacity, min: 1, max: WorldStateCapacity.MaxCellsPerRow);

        if (cells.Count <= effectiveCapacity) {
            return cells;
        }

        // FIFO by INSERTION POSITION: a brand-new key is always appended to the END and an existing key is replaced
        // IN PLACE, so index 0 is always the row's oldest surviving cell regardless of how recently any OTHER key
        // was last written — an in-place rewrite never moves a key to the back.
        evictedKey = cells[0].Key;

        var trimmed = new List<WorldStateCell>(capacity: (cells.Count - 1));

        for (var index = 1; (index < cells.Count); index++) {
            trimmed.Add(item: cells[index]);
        }

        return trimmed;
    }

    /// <summary>Composes ONE text-cell write onto <paramref name="row"/> — upsert-or-append plus eviction — running
    /// the SAME reserved-cell rule (<see cref="WorldStateReservedCells.TryValidateReservedCell"/>) the running world's
    /// own mutation pipeline runs, so a hand-typed <c>world.state.cell.set</c> text write and an owned-identity document write
    /// are refused by the identical rule rather than two readings of it.</summary>
    /// <param name="row">The carrying row (must declare <see cref="CellKind.Text"/>).</param>
    /// <param name="key">The cell key to write.</param>
    /// <param name="text">The text value.</param>
    /// <param name="cells">The composed cell list, on success (or the row's current cells on refusal).</param>
    /// <param name="evictedKey">The evicted key, or <see langword="null"/> when nothing was evicted.</param>
    /// <param name="reason">Why the write was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the write composed.</returns>
    public static bool TryComposeTextCell(WorldStateRow row, WorldCellName key, string text, out IReadOnlyList<WorldStateCell> cells, out WorldCellName? evictedKey, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: row);

        cells = (row.Cells ?? []);
        evictedKey = null;

        if (row.Kind != CellKind.Text) {
            reason = "is not a text row";

            return false;
        }

        if (!WorldStateReservedCells.TryValidateReservedCell(row: row, key: key, reason: out reason)) {
            return false;
        }

        var existing = (row.Cells ?? []);
        var addedNewKey = !ContainsKey(cells: existing, key: key);
        var replaced = false;
        var next = new List<WorldStateCell>(capacity: (existing.Count + 1));

        foreach (var cell in existing) {
            if (!replaced && (cell.Key == key)) {
                next.Add(item: new WorldStateCell(Key: key, Text: text));
                replaced = true;
            } else {
                next.Add(item: cell);
            }
        }

        if (!replaced) {
            next.Add(item: new WorldStateCell(Key: key, Text: text));
        }

        cells = ApplyEviction(row: row, cells: next, addedNewKey: addedNewKey, evictedKey: out evictedKey);

        return true;
    }
}
