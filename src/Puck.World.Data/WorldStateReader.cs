using System.Diagnostics.CodeAnalysis;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The ONE (row, key) → raw-value read over a world document's <c>state</c> section. Every live numeric read of a
/// declared cell resolves here — a world rule's gate comparand and its live copy operand, a rule effect's
/// read-modify-write, the <c>world.state</c> console read-backs, the HUD <c>state.&lt;row&gt;</c>/
/// <c>state.&lt;row&gt;.&lt;key&gt;</c> binding, and the <c>UpsertStateCell</c> Add compose arm — so no two of them
/// can drift in how a pair addresses a cell, or in what that cell currently holds.
/// </summary>
/// <remarks>
/// <para><b>The pair rule, once.</b> A null <c>key</c> means the row's slot cell
/// (<see cref="WorldStateRow.SlotKey"/>); a non-null one names a cell inside the row. Which pairs are ADMISSIBLE is a
/// separate, author-time question decided by <see cref="WorldStateRow.IsKeyed"/> at the doors that accept a pair (the
/// rule compiler, the whole-document validator, the <c>Generate</c> compose arm) — this reader only RESOLVES an
/// already-admitted pair, and a pair naming a row or cell the document does not declare reads as "absent" rather than
/// refusing: a mid-tick <c>RemoveStateRow</c> is the only way to get there, and the next install's recompile refuses
/// the rule outright if it can no longer resolve.</para>
/// <para><b>Why a null key can just BE the slot key here.</b> Resolving a null key to
/// <see cref="WorldStateRow.SlotKey"/> and scanning is equivalent to asking <see cref="WorldStateRow.IsSlot"/> first
/// and taking the row's single cell, and the equivalence is what lets one rule serve both the callers that pass an
/// already-resolved key and the HUD binding that passes null. It holds because a <c>$value</c> cell can only exist on
/// a slot-shaped row in the first place: the validator refuses the reserved slot key as an authored cell key on any
/// row that declares a capacity or carries a cell count other than one, and refuses it outright on a generator row,
/// at boot, on every mutation, and on every undo-replay entry. So an installed document that HAS a <c>$value</c> cell
/// is slot-shaped, and one that does not resolves to nothing under either reading.</para>
/// <para><b>The value is COMPUTED, not fetched.</b> A row declaring <see cref="WorldStateRow.Advance"/> stores a
/// BASE in its slot cell and advances from it with elapsed ticks; a KEYED row's own cell may independently declare
/// <see cref="WorldStateCell.Advance"/> the same way over its own base — the two never both name the SAME cell (the
/// slot cell may carry only the row's own trait), so this reader checks the row's trait first and only then the
/// cell's own, never both. Either way it returns <see cref="WorldStateAdvance.ComputeCurrentValue"/> at <c>tick</c>
/// for the advancing cell and the stored value for every other. This is the trait's ONLY application site, which is
/// what makes a reader and a writer unable to disagree: an <c>add</c> composes against what a reader sees (the
/// compose arm reads here too), a rule gates on it, a HUD gauge draws it, and <c>world.state</c> echoes it, all from
/// this one computation — and because <see cref="Reduce"/>/<see cref="ArgExtremum"/> already resolve EACH candidate
/// cell through this same seam rather than reading <see cref="WorldStateCell.Value"/> off the row directly, a
/// <c>$reduce:</c>/<c>$argmax:</c>/<c>$argmin:</c> operand over a table of independently advancing cells sees every
/// cell's LIVE value for free — no special case anywhere in either method.</para>
/// <para><b>Allocation-free.</b> The HUD path runs this once per bound element per frame, so this is an ordinal
/// linear scan over the row's own cells with no LINQ, no closure, and no intermediate collection — matching
/// <see cref="WorldDefinitionRows"/>'s own idiom — and it hands back a RAW value rather than a cell record, because
/// an advancing row's computed value has no stored <see cref="WorldStateCell"/> to hand back and minting one per
/// read would allocate on that per-frame path. An ordinary row's read allocates nothing at all; an advancing row's
/// pays only what <see cref="Puck.Maths.DiscreteMeasure"/>'s exact rational allocation costs for its magnitude.</para>
/// </remarks>
public static class WorldStateReader {
    /// <summary>Resolves one (row, key) pair against a document's live <c>state</c> section.</summary>
    /// <param name="definition">The document to read. The WHOLE document rather than just its rows: a cell's value
    /// is the document's answer, and what the value-over-time trait reads to compute one is a document-scoped
    /// question (see <paramref name="tick"/>).</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="key">The cell key inside the row, or <see langword="null"/> for the row's slot cell
    /// (<see cref="WorldStateRow.SlotKey"/>).</param>
    /// <param name="tick">The tick this read is answering AS OF — what an advancing row's value is computed at.
    /// Callers pass the tick their frame already knows: the server's completed tick on the authoritative side, the
    /// last delivered snapshot's tick on the client side (which IS a server tick, so it is comparable to an epoch;
    /// it lags by delivery, never by a different clock).</param>
    /// <param name="row">The named row, or <see langword="null"/> when the section declares none by that name.</param>
    /// <param name="rawValue">The addressed cell's live raw value, or <see langword="null"/> when the row declares no
    /// cell under that key — a distinct outcome from an unknown row, because a rule effect's read-modify-write treats
    /// an absent cell as zero but an absent ROW as nothing to write.</param>
    /// <param name="text">The addressed cell's text payload (<see cref="CellKind.Text"/> rows only), or
    /// <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the ROW resolved (whether or not it holds the addressed cell).</returns>
    public static bool TryRead(
        WorldDefinition definition,
        string rowName,
        string? key,
        ulong tick,
        [NotNullWhen(true)] out WorldStateRow? row,
        out long? rawValue,
        out string? text
    ) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        rawValue = null;
        text = null;
        row = WorldDefinitionRows.FindStateRow(rows: definition.State, name: rowName);

        if (row is null) {
            return false;
        }

        var target = (key ?? WorldStateRow.SlotKey.Value);

        foreach (var candidate in (row.Cells ?? [])) {
            if (string.Equals(a: candidate.Key, b: target, comparisonType: StringComparison.Ordinal)) {
                rawValue = (((row.Advance is { } advance) && (candidate.Key == WorldStateRow.SlotKey))
                    ? advance.ComputeCurrentValue(row: row, baseValue: candidate.Value, currentTick: tick)
                    : ((candidate.Advance is { } cellAdvance)
                        ? cellAdvance.ComputeCurrentValue(row: row, baseValue: candidate.Value, currentTick: tick)
                        : candidate.Value));
                text = candidate.Text;

                break;
            }
        }

        return true;
    }

    /// <summary>Reduces a keyed row's cell values with <paramref name="op"/>, resolving EACH cell through this same
    /// per-(row, key) seam as <see cref="TryRead"/> rather than walking the row's declared cell list raw — so a
    /// table whose cells independently advance (<see cref="WorldStateCell.Advance"/>) reduces over every cell's LIVE
    /// value for free, never a stale base read straight off <see cref="WorldStateCell.Value"/>.</summary>
    /// <param name="definition">The document to read.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="op">The reduction to apply. <see cref="WorldStateReduceOp.Count"/> answers with the row's cell
    /// count as an integer regardless of the row's <see cref="WorldStateRow.Kind"/>; the others preserve that
    /// kind.</param>
    /// <param name="tick">The tick this read is answering AS OF; forwarded to each per-cell <see cref="TryRead"/>.</param>
    /// <returns>The reduced value, or <see cref="FixedQ4816.Zero"/> when the row is absent or holds no cells.</returns>
    public static FixedQ4816 Reduce(WorldDefinition definition, string rowName, WorldStateReduceOp op, ulong tick) {
        if (!TryRead(definition: definition, rowName: rowName, key: null, tick: tick, row: out var declared, rawValue: out _, text: out _)) {
            return FixedQ4816.Zero;
        }

        var keys = (declared.Cells ?? []);

        if (op == WorldStateReduceOp.Count) {
            return FixedQ4816.FromInteger(value: keys.Count);
        }

        if (keys.Count == 0) {
            return FixedQ4816.Zero;
        }

        var isFixed = (declared.Kind == CellKind.Fixed);
        var hasAcc = false;
        var acc = FixedQ4816.Zero;

        foreach (var candidate in keys) {
            if (!TryRead(definition: definition, rowName: rowName, key: candidate.Key, tick: tick, row: out _, rawValue: out var raw, text: out _) || (raw is null)) {
                continue;
            }

            var value = (isFixed ? FixedQ4816.FromRawBits(value: raw.Value) : FixedQ4816.FromInteger(value: raw.Value));

            acc = (!hasAcc ? value : (op switch {
                WorldStateReduceOp.Sum => (acc + value),
                WorldStateReduceOp.Max => ((value > acc) ? value : acc),
                _ => ((value < acc) ? value : acc), // Min.
            }));
            hasAcc = true;
        }

        return acc;
    }

    /// <summary>Finds the winning cell's KEY over a keyed row under <paramref name="op"/>
    /// (<see cref="WorldStateReduceOp.Max"/> or <see cref="WorldStateReduceOp.Min"/>), resolving each candidate
    /// cell's value through this same per-(row, key) seam as <see cref="TryRead"/> rather than walking the row's
    /// declared cell list raw. A cell key that does not parse as a non-negative integer, or that
    /// <paramref name="isCandidateIndex"/> rejects, is excluded from the comparison; ties go to the LOWEST parsed
    /// index.</summary>
    /// <param name="definition">The document to read.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="op">The extremum to find (<see cref="WorldStateReduceOp.Max"/> or
    /// <see cref="WorldStateReduceOp.Min"/>).</param>
    /// <param name="tick">The tick this read is answering AS OF; forwarded to each per-cell <see cref="TryRead"/>.</param>
    /// <param name="isCandidateIndex">An optional additional filter over a cell key's parsed index (for example, a
    /// caller-side population-capacity bound). <see langword="null"/> admits every non-negative parsed index.</param>
    /// <returns>The winning cell's key, or <see langword="null"/> when the row is absent, holds no cells, or no cell
    /// key both parses and passes <paramref name="isCandidateIndex"/>.</returns>
    public static string? ArgExtremum(
        WorldDefinition definition,
        string rowName,
        WorldStateReduceOp op,
        ulong tick,
        Func<int, bool>? isCandidateIndex = null
    ) {
        if (!TryRead(definition: definition, rowName: rowName, key: null, tick: tick, row: out var declared, rawValue: out _, text: out _)) {
            return null;
        }

        var isFixed = (declared.Kind == CellKind.Fixed);
        var bestIndex = -1;
        string? bestKey = null;
        var bestValue = FixedQ4816.Zero;

        foreach (var candidate in (declared.Cells ?? [])) {
            if (!int.TryParse(s: candidate.Key, style: System.Globalization.NumberStyles.Integer, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var index)
                || (index < 0)
                || ((isCandidateIndex is not null) && !isCandidateIndex(index))) {
                continue;
            }

            if (!TryRead(definition: definition, rowName: rowName, key: candidate.Key, tick: tick, row: out _, rawValue: out var raw, text: out _) || (raw is null)) {
                continue;
            }

            var value = (isFixed ? FixedQ4816.FromRawBits(value: raw.Value) : FixedQ4816.FromInteger(value: raw.Value));
            var better = ((bestIndex < 0) || ((op == WorldStateReduceOp.Max) ? (value > bestValue) : (value < bestValue)));
            var tieLower = ((bestIndex >= 0) && (value == bestValue) && (index < bestIndex));

            if (better || tieLower) {
                bestIndex = index;
                bestKey = candidate.Key;
                bestValue = value;
            }
        }

        return bestKey;
    }
}
