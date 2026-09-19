using System.Diagnostics.CodeAnalysis;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // The clocks a compose applies its members at: the mutation's own tick pair, plus the document's dynamics rows
    // and simulation rate, so an eased cell's write kicks the follower it is actually chasing rather than reading a
    // rate of zero and resting on its stored target.
    private static ArenaTime ComposeTime(WorldDefinition definition, ulong tick, ulong engineTick) => new(
        Dynamics: definition.Dynamics,
        EngineTick: engineTick,
        Tick: tick,
        TicksPerSecond: definition.SimulationRateHz
    );
    // Loads one arena over a definition's own state section — the store a compose applies its state members through.
    // A submitted row declaration reaches this before any validator has seen it, so a section the arena cannot lay
    // out at all (a board over a topology the document does not declare, a vector row over an undeclared space) is
    // carried back as a refusal by the name the layout names rather than thrown out of the apply door.
    private static bool TryOpenComposeArena(WorldDefinition definition, in ArenaTime time, [NotNullWhen(true)] out StateArena? arena, out string reason) {
        try {
            return StateArena.TryCreate(
                arena: out arena,
                catalog: definition.StateCatalog,
                options: WorldSlotLanes.Options(definition: definition),
                reason: out reason,
                section: definition.StateRaw,
                time: in time
            );
        } catch (InvalidOperationException layout) {
            arena = null;
            reason = layout.Message;

            return false;
        }
    }
    // The candidate's state section is the arena's export: every exported row is the row the arena was built over
    // with its stored columns written back, so a WorldStateRow's own traits ride across and the cast is total.
    private static WorldDefinition ExportComposeArena(WorldDefinition definition, StateArena arena) {
        var exported = arena.ToRows();
        var rows = new WorldStateRow[exported.Count];

        for (var index = 0; (index < exported.Count); index++) {
            rows[index] = ((WorldStateRow)exported[index]);
        }

        return definition.WithWorldState(rows: rows);
    }
    // Applies one cell write through the arena's own kernels. `working` supplies the row RECORD — its kind, its
    // cycle and eviction declarations — so a same-batch row declaration ahead of this write is what the write sees;
    // every VALUE the write reads (a cycle's current token, an add's addend) comes from the arena, live at
    // `time`, and the store itself is the arena's, so envelope, capacity, eviction and clock settling are decided
    // once, where a rule firing decides them.
    private static bool TryApplyCellUpsert(StateArena arena, WorldDefinition working, WorldMutation.UpsertStateCell mutation, in ArenaTime time, out string reason, out CellName? evictedKey) {
        reason = string.Empty;
        evictedKey = null;
        // The one door: every row-existence and row-kind decision this write depends on is asked here, against
        // the candidate this compose has built so far, never at the console verb, which cannot know whether a
        // same-batch UpsertStateRow ahead of this one has already declared (or redeclared the kind of) the row
        // it names.
        if (WorldDefinitionRows.FindStateRow(
            rows: working.State,
            name: mutation.Row
        ) is not { } row) {
            reason = $"no state row named '{mutation.Row}' — declare it first with world.row.set state <json>";

            return false;
        }

        if (row.Inverse is { } inverse) {
            reason = $"state row '{mutation.Row}' is a derived board (inverse names '{inverse.Tokens}'/'{inverse.Codes}') — write those rows instead; the engine recomputes '{mutation.Row}' on install";

            return false;
        }

        if (row.Verdict is not null) {
            reason = WorldVerdict.RefuseWrite(row: row.Name);

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

        if (!arena.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: mutation.Row
        )) {
            reason = $"no state row named '{mutation.Row}' — declare it first with world.row.set state <json>";

            return false;
        }

        var rowOrdinal = handle.Ordinal;

        if (
            (mutation.Vector is not null) &&
            (row.Kind != CellKind.Vector)
        ) {
            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' is not vector-kind and takes a {StateSpelling.Kind(row.Kind)} operand, never a vector one";

            return false;
        }

        if (row.Kind == CellKind.Vector) {
            if (mutation.Kind != WorldDocumentWriteKind.Set) {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' — 'add' is refused on a vector-kind row";

                return false;
            }

            if (mutation.Text is not null) {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' is vector-kind and takes a vector operand, never a text one";

                return false;
            }

            if (mutation.CycleTokens is not null) {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' cycle is refused on a vector-kind row";

                return false;
            }

            if (!WorldStateSpaces.TryResolveSpace(
                spaces: working.Spaces,
                row: row,
                space: out var space,
                reason: out var spaceReason
            )) {
                reason = spaceReason;

                return false;
            }

            StateVector vectorToWrite;

            if (mutation.Vector is not null) {
                vectorToWrite = mutation.Vector;
            } else if (mutation.RawToken is { } rawToken) {
                if (!StateVector.TryParseBase64Url(
                    text: rawToken,
                    dimensions: space.Dimensions,
                    vector: out var parsedVector,
                    error: out var parseError
                )) {
                    reason = $"state row '{mutation.Row}' cell '{mutation.Key}' {parseError}";

                    return false;
                }

                vectorToWrite = parsedVector;
            } else {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' requires a vector operand";

                return false;
            }

            if (vectorToWrite.Dimensions != space.Dimensions) {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' vector dimensions {vectorToWrite.Dimensions} do not match space '{space.Name}' dimensions {space.Dimensions}";

                return false;
            }

            return TryStoreCarried(
                arena: arena,
                cellKey: cellKey,
                evictedKey: out evictedKey,
                keyText: mutation.Key,
                reason: out reason,
                rowName: mutation.Row,
                rowOrdinal: rowOrdinal,
                value: CellValue.Vector(components: vectorToWrite.Memory)
            );
        }

        // Whether this write is a text write is a fact of the write, not the row — a text write always
        // carries a non-null Text (even ""), a numeric one never does. Asking it this way
        // (rather than switching on row.Kind) is what lets a kind-mismatched write refuse by name instead of
        // silently composing against the wrong field: a numeric write against a text row would
        // otherwise fall into this arm with Text null and overwrite the cell with an empty string.
        var isTextWrite = (mutation.Text is not null);
        // A cycle on a text row: the next token after the one the live cell reads, wrapping; the write
        // is then an ordinary text set of that token.
        var isTextCycle = ((row.Kind == CellKind.Text) && (mutation.CycleTokens is { Count: >= 2 }));

        if (
            isTextCycle &&
            (mutation.Kind != WorldDocumentWriteKind.Set)
        ) {
            reason = $"state row '{mutation.Row}' cell '{mutation.Key}' cycle needs a set write";

            return false;
        }

        if (
            isTextWrite ||
            isTextCycle
        ) {
            if (row.Kind != CellKind.Text) {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' is not a text row";

                return false;
            }

            var textToWrite = mutation.Text!;

            if (isTextCycle) {
                var currentText = ((arena.Catalog.Keys.TryResolve(
                    key: out var currentKey,
                    name: cellKey
                ) &&
                    arena.TryReadLive(
                    key: currentKey,
                    rowOrdinal: rowOrdinal,
                    time: in time,
                    value: out var carried
                ) &&
                    (carried.Kind == CellKind.Text))
                    ? carried.AsText
                    : null
                );

                textToWrite = NextInCycle(
                    tokens: mutation.CycleTokens!,
                    matches: token => string.Equals(
                        a: token,
                        b: currentText,
                        comparisonType: StringComparison.Ordinal
                    )
                );
            }

            return TryStoreCarried(
                arena: arena,
                cellKey: cellKey,
                evictedKey: out evictedKey,
                keyText: mutation.Key,
                reason: out reason,
                rowName: mutation.Row,
                rowOrdinal: rowOrdinal,
                value: CellValue.Text(value: textToWrite)
            );
        }

        // The reverse kind mismatch: a numeric operand against a Text-kind row. This, and the bool+add
        // refusal below, are the two kind-dependent refusals the console verb used to ask before submitting —
        // asked here so they see the same candidate row the existence check above just resolved, rather than
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
                if (!CellValue.TryParse(
                    kind: row.Kind,
                    reason: out var cycleReason,
                    token: cycleTokens[index],
                    value: out var carried
                )) {
                    reason = $"state row '{mutation.Row}' cell '{mutation.Key}' {cycleReason}";

                    return false;
                }

                parsed[index] = carried.Raw;
            }

            var live = (ReadLiveNumber(
                arena: arena,
                cellKey: cellKey,
                rowOrdinal: rowOrdinal,
                time: in time
            ) ?? long.MinValue);
            var at = Array.IndexOf(
                array: parsed,
                value: live
            );

            operand = parsed[((at < 0)
                ? 0
                : ((at + 1) % parsed.Length))];
        } else if (mutation.RawToken is { } rawToken) {
            if (!CellValue.TryParse(
                kind: row.Kind,
                reason: out var tokenReason,
                token: rawToken,
                value: out var parsedToken
            )) {
                reason = $"state row '{mutation.Row}' cell '{mutation.Key}' {tokenReason}";

                return false;
            }

            operand = parsedToken.Raw;
        } else {
            operand = mutation.Value;
        }

        return TryStoreNumber(
            arena: arena,
            cellKey: cellKey,
            evictedKey: out evictedKey,
            keyText: mutation.Key,
            operand: operand,
            reason: out reason,
            row: row,
            rowName: mutation.Row,
            rowOrdinal: rowOrdinal,
            time: in time,
            write: ((mutation.Kind == WorldDocumentWriteKind.Add)
                ? StateWriteKind.Add
                : StateWriteKind.Set
            )
        );
    }
    // Composes one cell removal onto the row it names, on the same terms as TryApplyCellUpsert.
    private static bool TryApplyCellRemove(StateArena arena, WorldDefinition working, WorldMutation.RemoveStateCell mutation, out string reason) {
        reason = string.Empty;

        if (WorldDefinitionRows.FindStateRow(
            rows: working.State,
            name: mutation.Row
        ) is not { } row) {
            reason = $"no state row named '{mutation.Row}'";

            return false;
        }

        if (row.Inverse is { } inverse) {
            reason = $"state row '{mutation.Row}' is a derived board (inverse names '{inverse.Tokens}'/'{inverse.Codes}') — write those rows instead; the engine recomputes '{mutation.Row}' on install";

            return false;
        }

        if (row.Verdict is not null) {
            reason = WorldVerdict.RefuseWrite(row: row.Name);

            return false;
        }

        if (
            !arena.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: mutation.Row
        ) ||
            !CellName.TryParse(
            candidate: mutation.Key,
            name: out var cellKey,
            reason: out _
        ) ||
            !arena.Catalog.Keys.TryResolve(
            key: out var key,
            name: cellKey
        )
        ) {
            reason = $"state row '{mutation.Row}' has no cell keyed '{mutation.Key}'";

            return false;
        }

        if (!arena.TryRemove(
            key: key,
            reason: out var removeReason,
            rowOrdinal: handle.Ordinal
        )) {
            reason = removeReason;

            return false;
        }

        return true;
    }
    // The live number a cell reads at `time`, or null when the row holds no cell under the key.
    private static long? ReadLiveNumber(StateArena arena, int rowOrdinal, CellName cellKey, in ArenaTime time) => ((arena.Catalog.Keys.TryResolve(
        key: out var key,
        name: cellKey
    ) &&
        arena.TryReadLiveNumber(
        key: key,
        rowOrdinal: rowOrdinal,
        time: in time,
        value: out var value
    ))
        ? value
        : null
    );
    // The one door a composed numeric operand reaches the arena through: the live write — which rebases an advancing
    // cell, kicks an easing one, and leaves a rotating cell's clock where it is — or a mint when the row holds no
    // cell under the key yet.
    private static bool TryStoreNumber(StateArena arena, int rowOrdinal, WorldStateRow row, CellName cellKey, string rowName, string keyText, long operand, StateWriteKind write, in ArenaTime time, out string reason, out CellName? evictedKey) {
        if (!TryLocateCell(
            arena: arena,
            cellKey: cellKey,
            evictedKey: out evictedKey,
            key: out var key,
            keyText: keyText,
            mints: out var mints,
            present: out _,
            reason: out reason,
            rowName: rowName,
            rowOrdinal: rowOrdinal
        )) {
            return false;
        }

        if (!mints) {
            return arena.TryWriteLive(
                key: key,
                operand: operand,
                reason: out reason,
                rowOrdinal: rowOrdinal,
                time: in time,
                write: write
            );
        }

        // A mint carries the operand itself: an add against a cell the row does not hold adds to nothing, which is
        // the operand the row's own envelope then admits.
        return TryMintCell(
            arena: arena,
            cellKey: cellKey,
            evictedKey: ref evictedKey,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            value: (row.Kind switch {
                CellKind.Bool => CellValue.Bool(value: (operand != 0L)),
                CellKind.Fixed => CellValue.Fixed(rawBits: operand),
                _ => CellValue.Int(value: operand),
            })
        );
    }
    // The text and vector counterpart of TryStoreNumber: neither kind carries a value-over-time trait, so the
    // carried value is stored as it stands.
    private static bool TryStoreCarried(StateArena arena, int rowOrdinal, CellName cellKey, string rowName, string keyText, CellValue value, out string reason, out CellName? evictedKey) {
        if (!TryLocateCell(
            arena: arena,
            cellKey: cellKey,
            evictedKey: out evictedKey,
            key: out var key,
            keyText: keyText,
            mints: out var mints,
            present: out _,
            reason: out reason,
            rowName: rowName,
            rowOrdinal: rowOrdinal
        )) {
            return false;
        }

        if (!mints) {
            return arena.TryWrite(
                key: key,
                reason: out reason,
                rowOrdinal: rowOrdinal,
                value: value
            );
        }

        return TryMintCell(
            arena: arena,
            cellKey: cellKey,
            evictedKey: ref evictedKey,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            value: value
        );
    }
    // Reports whether the row already holds the addressed cell, whether a write would mint one, and which key that
    // mint would evict. Every admission the write itself needs — the envelope, the reserved-cell rule, the capacity
    // and its eviction — belongs to the arena door below, not here.
    private static bool TryLocateCell(StateArena arena, int rowOrdinal, CellName cellKey, string rowName, string keyText, out CellKey key, out bool present, out bool mints, out CellName? evictedKey, out string reason) {
        evictedKey = null;
        key = default;
        mints = false;
        present = false;

        // The key is interned before the row is addressed: a lattice or ring row resolves a cell's position from
        // the interned NAME, so a write naming one of its own addresses reaches it rather than reading as absent.
        if (!arena.Catalog.Keys.TryIntern(
            key: out key,
            name: cellKey,
            reason: out var internReason
        )) {
            reason = $"state row '{rowName}' cell '{keyText}' {internReason}";

            return false;
        }

        reason = string.Empty;
        present = arena.TryCellSlot(
            key: key,
            rowOrdinal: rowOrdinal,
            slot: out _
        );
        // Only a keyed or ordered row mints. Every other shape's addresses are its own — a lattice's topology keys,
        // a ring's slot indices, a slot row's one reserved key — so a write naming an address it does not hold goes
        // to the write door and is refused there by the address, never minted beside them.
        mints = (!present && (arena.Layout[rowOrdinal].Shape is (RowShape.Keyed or RowShape.Ordered)));

        if (
            mints &&
            arena.TryEvictionVictim(
            key: out var victim,
            rowOrdinal: rowOrdinal
        )
        ) {
            evictedKey = arena.Catalog.Keys[victim];
        }

        return true;
    }
    private static bool TryMintCell(StateArena arena, int rowOrdinal, CellName cellKey, CellValue value, ref CellName? evictedKey, out string reason) {
        if (arena.TryMint(
            key: out _,
            name: cellKey,
            reason: out reason,
            rowOrdinal: rowOrdinal,
            value: value
        )) {
            return true;
        }

        evictedKey = null;

        return false;
    }
}
