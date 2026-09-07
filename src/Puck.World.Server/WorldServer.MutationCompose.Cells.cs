using System.Diagnostics.CodeAnalysis;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Composes one cell write onto the row it names, reading every row-existence and row-kind fact against `rows` —
    // the candidate document's rows the caller has built so far, so a same-batch row declaration ahead of this
    // write is what the write sees. `rows` is read directly rather than through a WorldDefinition so a batch's
    // shared workspace list can be handed in live, mid-placement, without wrapping it into a section first. Hands
    // back the written row; the caller places it, whether into a fresh row list or a batch's workspace.
    private static bool TryComposeCellUpsert(IReadOnlyList<WorldStateRow> rows, WorldMutation.UpsertStateCell mutation, ulong tick, [NotNullWhen(true)] out WorldStateRow? composed, out string reason, out CellName? evictedKey) {
        composed = null;
        reason = string.Empty;
        evictedKey = null;
        // The one door: every row-existence and row-kind decision this write depends on is asked here, against
        // the candidate this batch has built so far — never at the console verb, which cannot know whether a
        // same-batch UpsertStateRow ahead of this one has already declared (or redeclared the kind of) the row
        // it names.
        if (WorldDefinitionRows.FindStateRow(
            rows: rows,
            name: mutation.Row
        ) is not { } row) {
            reason = $"no state row named '{mutation.Row}' — declare it first with world.row.set state <json>";

            return false;
        }

        if (row.Inverse is { } inverse) {
            reason = $"state row '{mutation.Row}' is a derived board (inverse names '{inverse.Tokens}'/'{inverse.Codes}') — write those rows instead; the engine recomputes '{mutation.Row}' on install";

            return false;
        }

        if (!CellName.TryParse(
            candidate: mutation.Key,
            name: out var cellKey,
            reason: out var keyReason
        )) {
            reason = $"state row '{mutation.Row}' cell key '{mutation.Key}' {keyReason}";

            return false;
        }

        // Whether this write is a text write is a fact of the write, not the row — a text write always
        // carries a non-null Text (even ""), a numeric one never does. Asking it this way
        // (rather than switching on row.Kind) is what lets a kind-mismatched write refuse by name instead of
        // silently composing against the wrong field: a numeric write against a text row would
        // otherwise fall into this arm with Text null and overwrite the cell with an empty string.
        var isTextWrite = (mutation.Text is not null);

        // A text row's cell carries a literal string, never a numeric operand: world.state.cell.set's text
        // arm is this shape's one ingress, always submitting Kind=Set, so the Add/advance machinery below never applies.
        // The whole upsert-or-append-plus-eviction composition (including the reserved-key rule — a text row
        // is never a generator, so its only legitimate reserved key is the slot cell) delegates to
        // StateCellWriter — the shared pure function an owned-identity document write (which has no
        // ordered mutation domain of its own) also runs, so the two can never disagree about a victim or a
        // reserved-cell refusal. TryComposeTextCell itself refuses by name when row.Kind is not Text, which is
        // this arm's one check for "a text operand against a numeric/bool row".
        // A cycle on a text row: the next token after the one the live cell reads, wrapping; the write
        // is then an ordinary text set of that token.
        var isTextCycle = ((row.Kind == CellKind.Text) && (mutation.CycleTokens is { Count: >= 2 }));

        if (isTextCycle && (mutation.Kind != WorldDocumentWriteKind.Set)) {
            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' cycle needs a set write";

            return false;
        }

        if (isTextWrite || isTextCycle) {
            var textToWrite = mutation.Text!;

            if (isTextCycle) {
                _ = StateReader.TryRead(
                    rows: rows,
                    rowName: mutation.Row,
                    key: mutation.Key,
                    tick: tick,
                    row: out _,
                    rawValue: out _,
                    text: out var currentText
                );
                textToWrite = NextInCycle(
                    tokens: mutation.CycleTokens!,
                    matches: token => string.Equals(a: token, b: currentText, comparisonType: StringComparison.Ordinal)
                );
            }

            if (!StateCellWriter.TryComposeTextCell(
                cells: out var textCells,
                evictedKey: out evictedKey,
                key: cellKey,
                reason: out var composeTextReason,
                row: row,
                text: textToWrite
            )) {
                    reason = $"state row '{mutation.Row}' cell '{mutation.Key}' {composeTextReason}";

                return false;
            }

            composed = (row with { Cells = textCells });

            return true;
        }

        // The reverse kind mismatch: a numeric operand against a Text-kind row. This, and the bool+add
        // refusal below, are the two kind-dependent refusals the console verb used to ask before submitting —
        // moved here so they see the same candidate row the existence check above just resolved, rather than
        // whatever the live definition happened to hold at text-submit time.
        if (row.Kind == CellKind.Text) {
            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' is text-kind and takes a text operand, never a numeric one";

            return false;
        }

        if (
            (mutation.Kind == WorldDocumentWriteKind.Add) &&
            (row.Kind == CellKind.Bool)
        ) {
            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' — 'add' is refused on a bool-kind row";

            return false;
        }

        if (
            (mutation.CycleTokens is not null) &&
            ((mutation.CycleTokens.Count < 2) || (mutation.Kind != WorldDocumentWriteKind.Set))
        ) {
            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' cycle needs at least two tokens and a set write";

            return false;
        }

        // The honest encoding for a payload whose shape depends on the row's kind: a console write carries
        // the un-interpreted wire token (RawToken) because it cannot know Fixed-vs-Int-vs-Bool before this
        // row's kind resolves against the candidate; a caller that already knows the kind (the rule-effect
        // engine, which reads the destination row itself before submitting) carries the resolved Value
        // directly. See WorldMutation.UpsertStateCell.RawToken's remarks.
        long operand;

        if (mutation.CycleTokens is { Count: >= 2 } cycleTokens) {
            // A cycle on a numeric row: every token must parse against this row's kind; the operand is the
            // token after the one the live value equals (wrapping), else the first. The live value is the
            // same read every gate and binding runs, so an advancing row cycles from what a reader sees.
            var parsed = new long[cycleTokens.Count];

            for (var index = 0; (index < parsed.Length); index++) {
                if (!StateCellWriter.TryParseNumericToken(
                    kind: row.Kind,
                    token: cycleTokens[index],
                    value: out parsed[index],
                    reason: out var cycleReason
                )) {
                            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' {cycleReason}";

                    return false;
                }
            }

            _ = StateReader.TryRead(
                rows: rows,
                rowName: mutation.Row,
                key: mutation.Key,
                tick: tick,
                row: out _,
                rawValue: out var live,
                text: out _
            );
            var at = Array.IndexOf(
                array: parsed,
                value: (live ?? long.MinValue)
            );

            operand = parsed[((at < 0)
                ? 0
                : ((at + 1) % parsed.Length))];
        } else if (mutation.RawToken is { } rawToken) {
            if (!StateCellWriter.TryParseNumericToken(
                kind: row.Kind,
                token: rawToken,
                value: out operand,
                reason: out var tokenReason
            )) {
                    reason = $"state row '{mutation.Row}' cell '{mutation.Key}' {tokenReason}";

                return false;
            }
        } else {
            operand = mutation.Value;
        }

        // The Add operand comes from the same live read every gate, binding, and read-back runs — rather than from
        // the stored cell. On an ordinary row the two are the same value, so this arm keeps
        // the read-modify-write-onto-the-base behaviour it always had. On an advancing row they differ, and
        // the live value is the right operand: the stored cell there is a base the row has been accumulating
        // away from, so adding to it would silently discard every unit gained since the epoch (a regen row
        // sitting at a live 41 taking a -10 would land on -10, not 31). Add means "add to what a reader
        // sees"; RebaseCellTraits then makes that sum the new base and starts the accumulation again from
        // this tick, so the row keeps advancing from the value the author just composed.
        _ = StateReader.TryRead(
            rows: rows,
            rowName: mutation.Row,
            key: mutation.Key,
            tick: tick,
            row: out var addendRow,
            rawValue: out var addend,
            text: out _
        );

        // A cycling cell is the one exception: its stored value is a phase and its live value the rotation
        // the trait carried that phase to, so an add turns the phase by the operand rather than baking the
        // tick's rotation into it (which would double the turn on the next read).
        if (
            (addendRow is not null) &&
            CellName.TryParse(candidate: mutation.Key, name: out var addendKey, reason: out _) &&
            (StateRows.FindCell(cells: addendRow.Cells, key: addendKey) is { } phaseCell) &&
            ((phaseCell.Cycle is not null) || ((phaseCell.Key == WorldStateRow.SlotKey) && (addendRow.Cycle is not null)))
        ) {
            addend = phaseCell.Value;
        }

        long value;

        try {
            value = ((mutation.Kind == WorldDocumentWriteKind.Add)
                ? checked(((addend ?? 0L) + operand))
                : operand
            );
        } catch (OverflowException) {
            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' overflowed";

            return false;
        }

        // The engine-minted-cell rule, asked at the verb so the operator reads why the cell they just typed
        // was refused rather than a whole-document validation error. Same code, not a second reading: the
        // document walk (boot, every mutation, every undo-replay entry) calls the identical
        // StateReservedCells rule, so the two can never disagree about which reserved keys a row mints.
        if (!StateReservedCells.TryValidateReservedCell(
            key: cellKey,
            reason: out var reservedReason,
            row: row
        )) {
            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' {reservedReason}";

            return false;
        }

        // UpsertStateCell carries only a scalar value — a cell's own advance rate, dynamics reference or
        // cycle are authored only through a whole-row UpsertStateRow — so a base-value write here
        // preserves whatever the existing cell already declared rather than silently deleting it;
        // RebaseCellTraits (below TryCompose) then re-bases the preserved advance/dynamics trait to this
        // tick, exactly as it already does for a row-level trait's slot cell. A cycle is never rebased:
        // its stored value is the phase, so the write itself is the whole operation.
        var existingCell = StateRows.FindCell(
            cells: row.Cells,
            key: cellKey
        );
        var existingAdvance = existingCell?.Advance;
        var existingDynamics = existingCell?.Dynamics;
        var existingCycle = existingCell?.Cycle;
        var isNewKey = !StateCellWriter.ContainsKey(
            cells: (row.Cells ?? []),
            key: cellKey
        );
        var cells = Upsert(
            list: (row.Cells ?? []),
            item: new StateCell(
                Key: cellKey,
                Value: value,
                Advance: existingAdvance,
                Dynamics: existingDynamics,
                Cycle: existingCycle,
                Visibility: existingCell?.Visibility,
                Observation: existingCell?.Observation
            ),
            keyOf: static (StateCell cell) => cell.Key
        );

        cells = StateCellWriter.ApplyEviction(
            addedNewKey: isNewKey,
            cells: cells,
            evictedKey: out evictedKey,
            row: row
        );
        composed = (row with { Cells = cells });

        return true;
    }
    // Composes one cell removal onto the row it names, on the same terms as TryComposeCellUpsert.
    private static bool TryComposeCellRemove(IReadOnlyList<WorldStateRow> rows, WorldMutation.RemoveStateCell mutation, [NotNullWhen(true)] out WorldStateRow? composed, out string reason) {
        composed = null;
        reason = string.Empty;
        if (WorldDefinitionRows.FindStateRow(
            rows: rows,
            name: mutation.Row
        ) is not { } row) {
            reason = $"no state row named '{mutation.Row}'";

            return false;
        }

        if (row.Inverse is { } inverse) {
            reason = $"state row '{mutation.Row}' is a derived board (inverse names '{inverse.Tokens}'/'{inverse.Codes}') — write those rows instead; the engine recomputes '{mutation.Row}' on install";

            return false;
        }

        if (!Remove(
            list: (row.Cells ?? []),
            key: mutation.Key,
            keyOf: static (StateCell cell) => cell.Key,
            result: out var cells
        )) {
            reason = $"state row '{mutation.Row}' has no cell keyed '{mutation.Key}'";

            return false;
        }

        composed = (row with { Cells = cells });

        return true;
    }
}
