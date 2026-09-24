using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler.Lowering;

// `state { world { table/slot/row declarations } }` (the concise-authoring surface over StateRow) — see
// src/Puck.World.Transpiler/README.md's syntax-contract section for the exact grammar, lowering, and refusals.
public static partial class WorldDocumentEmitter {
    // A row's kind is inferred rather than authored, so this reads straight off Puck.State.CellKind rather than a
    // construct member's admitted-word list.
    private static readonly HashSet<string> AdmittedRowKinds = [.. Enum.GetNames<CellKind>()];
    private static readonly HashSet<string> TableCellModifiers = WorldConstructs.CellModifiersOf(enclosing: "world", keyword: "table");

    private static bool LowerStateSectionBlock(BlockNode block, JsonObject parent, DocumentScope scope) {
        if (parent["state"] is not JsonObject stateObj) {
            stateObj = [];
            parent["state"] = stateObj;
        }

        var statePointer = $"{scope.CurrentPointer}/state";
        var oldPointer = scope.CurrentPointer;

        scope.SourceMap?.Register(
            jsonPointer: statePointer,
            span: block.Span
        );

        // Pre-pass: Lower spaces block first so that world block can reference declared spaces
        foreach (var stmt in block.Statements) {
            if (stmt is BlockNode { Identifier: "spaces", Name: null, Target: null } spacesBlock) {
                ValidateAndLowerSpacesBlock(
                    block: spacesBlock,
                    scope: scope,
                    stateObj: stateObj,
                    statePointer: statePointer
                );
            }
        }

        foreach (var stmt in block.Statements) {
            if (stmt is BlockNode { Identifier: "spaces", Name: null, Target: null }) {
                continue;
            }

            if (stmt is BlockNode { Identifier: "world", Name: null, Target: null } worldBlock) {
                LowerStateWorldBlock(
                    block: worldBlock,
                    scope: scope,
                    stateObj: stateObj,
                    statePointer: statePointer
                );

                continue;
            }

            if (stmt is RecordDeclarationNode recordNode) {
                var records = ((stateObj["records"] as JsonArray) ?? new JsonArray());

                stateObj["records"] = records;
                records.Add(item: LowerRecordDeclaration(declaration: recordNode, pointer: $"{statePointer}/records/{records.Count}", scope: scope, state: stateObj));
                continue;
            }

            if (stmt is StatePoolDeclarationNode poolNode) {
                var pools = ((stateObj["pools"] as JsonArray) ?? new JsonArray());

                stateObj["pools"] = pools;
                scope.SourceMap?.Register(jsonPointer: $"{statePointer}/pools/{pools.Count}", span: poolNode.Span);
                pools.Add(item: LowerPoolDeclaration(declaration: poolNode, scope: scope));
                continue;
            }
            if (stmt is StatePairPoolDeclarationNode pairPoolNode) {
                var pairPools = ((stateObj["pairPools"] as JsonArray) ?? new JsonArray());

                stateObj["pairPools"] = pairPools;
                scope.SourceMap?.Register(jsonPointer: $"{statePointer}/pairPools/{pairPools.Count}", span: pairPoolNode.Span);
                pairPools.Add(item: LowerPairPoolDeclaration(declaration: pairPoolNode, scope: scope));
                continue;
            }

            if (stmt is PropertyNode { Name: "world" } prop) {
                if (scope.Annotations.ContainsKey(key: "StateWorldDeclarationBlock") ||
                    scope.Annotations.ContainsKey(key: "StateWorldSqlForm")) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateWorldSectionMixed,
                        message: "'state.world' is authored more than once — write it either as the array form ('world [ ]') or the declaration block ('world { }'), never both",
                        span: prop.Span
                    );
                    continue;
                }
                scope.Annotations["StateWorldArrayForm"] = true;
            }

            scope.CurrentPointer = statePointer;
            ProcessStatement(
                scope: scope,
                statement: stmt,
                target: stateObj
            );
        }

        scope.CurrentPointer = oldPointer;

        return true;
    }
    private static void LowerStateWorldBlock(BlockNode block, JsonObject stateObj, DocumentScope scope, string statePointer) {
        // Authoring is judged per scope: rows a `use` stamped here were authored by that module, so a block written
        // beside them appends to them rather than authoring the section twice.
        if (scope.Annotations.ContainsKey(key: "StateWorldArrayForm") ||
            scope.Annotations.ContainsKey(key: "StateWorldDeclarationBlock")) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateWorldSectionMixed,
                message: "'state.world' is authored more than once — write it either as the array form ('world [ ]') or the declaration block ('world { }'), never both",
                span: block.Span
            );

            return;
        }

        scope.Annotations["StateWorldDeclarationBlock"] = true;

        JsonArray worldArr;

        if (stateObj["world"] is JsonArray existingArr) {
            worldArr = existingArr;
        } else {
            worldArr = [];
            stateObj["world"] = worldArr;
        }

        var worldPointer = $"{statePointer}/world";

        scope.SourceMap?.Register(
            jsonPointer: worldPointer,
            span: block.Span
        );

        var seenNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var pendingReferences = new List<PendingStateReference>();
        var totalVectorBytes = 0L;

        foreach (var (stmt, rowScope) in Expand(
            block.Statements,
            scope
        )) {
            JsonObject? companionRow = null;
            var latticesBefore = ((stateObj["lattices"] as JsonArray)?.Count ?? 0);
            var rowObj = (stmt switch {
                StateTableDeclarationNode table => LowerStateTableDeclaration(
                    companionRow: out companionRow,
                    scope: rowScope,
                    table: table,
                    totalVectorBytes: ref totalVectorBytes
                ),
                StateSlotDeclarationNode slot => LowerStateSlotDeclaration(
                    scope: rowScope,
                    slot: slot,
                    totalVectorBytes: ref totalVectorBytes
                ),
                StatePileDeclarationNode pile => LowerStatePileDeclaration(
                    pending: pendingReferences,
                    pile: pile,
                    scope: rowScope
                ),
                StateGridDeclarationNode grid => LowerStateGridDeclaration(
                    grid: grid,
                    pending: pendingReferences,
                    scope: rowScope,
                    stateObj: stateObj
                ),
                BlockNode { Identifier: "row", Name: null, Target: null } rowBlock => LowerBlockToObject(
                    block: rowBlock,
                    scope: rowScope
                ),
                _ => null,
            });

            if (rowObj is null) {
                ReportUnrecognizedSectionStatement(
                    admitted: "'table', 'slot', 'pile', 'grid', and 'row' declarations",
                    scope: rowScope,
                    section: "state.world",
                    stmt: stmt
                );

                continue;
            }

            if (
                (rowObj["name"] is JsonValue nameVal) &&
                nameVal.TryGetValue<string>(value: out var name) &&
                !seenNames.Add(item: name)
            ) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationDuplicateName,
                    message: $"'state.world' declares row '{name}' more than once",
                    span: stmt.Span
                );
            }

            var rowIdx = worldArr.Count;

            rowScope.SourceMap?.Register(
                jsonPointer: $"{worldPointer}/{rowIdx}",
                span: stmt.Span
            );
            worldArr.AppendNode(item: rowObj);
            ResolveRowEnum(
                pointer: $"{worldPointer}/{rowIdx}",
                row: rowObj,
                scope: rowScope,
                state: stateObj,
                statement: stmt
            );

            // A grid also declares the lattice its board lies over, authored by the same declaration.
            if (((stateObj["lattices"] as JsonArray)?.Count is { } latticesAfter) && (latticesAfter > latticesBefore)) {
                rowScope.SourceMap?.Register(
                    jsonPointer: $"{statePointer}/lattices/{latticesBefore}",
                    span: stmt.Span
                );
            }

            if (companionRow is not null) {
                if ((companionRow["name"]?.ToString() is { } compName) && !seenNames.Add(item: compName)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.EmbedsInvalid,
                        message: $"Companion vector table name '{compName}' collides with an existing row name.",
                        span: stmt.Span
                    );
                } else {
                    var compIdx = worldArr.Count;

                    rowScope.SourceMap?.Register(
                        jsonPointer: $"{worldPointer}/{compIdx}",
                        span: stmt.Span
                    );
                    worldArr.AppendNode(item: companionRow);
                }
            }
        }

        ValidateStateCrossReferences(
            pending: pendingReferences,
            scope: scope,
            worldArr: worldArr
        );
    }
    private static JsonObject LowerStateTableDeclaration(
        StateTableDeclarationNode table,
        DocumentScope scope,
        ref long totalVectorBytes,
        out JsonObject? companionRow
    ) {
        companionRow = null;
        table = table with { Kind = ((table.Enum is null) ? InferTableKind(scope: scope, table: table) : "Int") };
        ValidateStateRowName(
            kind: "table",
            name: table.Name,
            scope: scope,
            span: table.Span
        );
        ValidateStateKind(
            kind: table.Kind,
            rowName: table.Name,
            scope: scope,
            span: table.Span
        );

        var rowObj = new JsonObject {
            ["name"] = table.Name,
            ["kind"] = table.Kind,
        };

        if (table.Enum is { } tableEnum) {
            rowObj["enum"] = tableEnum;
        }

        var rootObj = ((scope.Annotations.TryGetValue(key: "WorldDocumentRoot", value: out var rObj) && (rObj is JsonObject ro)) ? ro : new JsonObject());

        var (spaceName, spaceInfo) = ResolveVectorSpace(
            declarationKind: "row",
            kind: table.Kind,
            missingDeclContext: "table",
            modifiers: table.Modifiers,
            name: table.Name,
            rootObj: rootObj,
            rowObj: rowObj,
            scope: scope,
            span: table.Span
        );

        var cellsArr = new JsonArray();
        var seenKeys = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var cell in table.Cells) {
            if (!seenKeys.Add(item: cell.Key)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationDuplicateName,
                    message: $"table '{table.Name}' declares cell '{cell.Key}' more than once",
                    span: cell.Span
                );
            }
            if (cell.Key.StartsWith(value: '$')) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationReservedKey,
                    message: $"table '{table.Name}' cell key '{cell.Key}' carries the reserved '$' prefix — reserved keys are engine-minted",
                    span: cell.Span
                );
            }

            var cellObj = new JsonObject { ["key"] = cell.Key };

            if (table.Kind == "Vector") {
                cellObj["value"] = LowerVectorCellValue(
                    context: $"table '{table.Name}' cell '{cell.Key}'",
                    expr: cell.Value,
                    scope: scope,
                    spaceName: (spaceName ?? "")
                );
            } else {
                cellObj["value"] = LowerStateScalarValue(
                    context: $"table '{table.Name}' cell '{cell.Key}'",
                    expr: cell.Value,
                    kind: table.Kind,
                    scope: scope
                );
            }

            var sawCellAdvance = false;
            var sawCellNone = false;

            foreach (var modifier in cell.Modifiers) {
                switch (modifier.Name) {
                    case "advance" when AdmitsKind(
                        enclosing: "world",
                        keyword: "table",
                        kind: table.Kind,
                        member: "advance",
                        position: WorldMemberPosition.Cell,
                        scope: scope
                    ):
                        if (sawCellAdvance) {
                            scope.Diagnostics.ReportError(
                                code: PuckDiagnosticCodes.StateDeclarationBehaviorConflict,
                                message: $"table '{table.Name}' cell '{cell.Key}' declares 'advance' more than once",
                                span: modifier.Span
                            );

                            break;
                        }

                        sawCellAdvance = true;
                        cellObj["advance"] = LowerStateAdvanceModifier(
                            context: $"table '{table.Name}' cell '{cell.Key}' advance",
                            modifier: modifier,
                            scope: scope
                        );

                        break;
                    case "advance":
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted,
                            message: $"'advance' is only legal where the row's kind is {KindList(kinds: AdmittedKindsOf(
                                enclosing: "world",
                                keyword: "table",
                                member: "advance",
                                position: WorldMemberPosition.Cell,
                                scope: scope
                            ))} — table '{table.Name}' cell '{cell.Key}' is {table.Kind}",
                            span: modifier.Span
                        );

                        break;
                    case "behavior":
                        sawCellNone = LowerStateBehaviorModifier(
                            cellObj: cellObj,
                            context: $"table '{table.Name}' cell '{cell.Key}'",
                            modifier: modifier,
                            scope: scope
                        );

                        break;
                    default:
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
                            message: $"'{modifier.Name}' is not a modifier a table cell admits — expected 'advance' or 'behavior'",
                            span: modifier.Span
                        );

                        break;
                }
            }

            if (sawCellAdvance && sawCellNone) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationBehaviorConflict,
                    message: $"table '{table.Name}' cell '{cell.Key}' declares both 'advance' and 'behavior(none)' — a cell opting out of its row's behavior carries no trait of its own",
                    span: cell.Span
                );
            }

            cellsArr.AppendNode(item: cellObj);
        }

        if (table.Initializer is not null) {
            var initVal = DocumentLowering.LowerValue(expr: table.Initializer, scope: scope);

            if (initVal is JsonArray initArr) {
                for (var i = 0; (i < initArr.Count); i++) {
                    cellsArr.AppendNode(item: new JsonObject {
                        ["key"] = i.ToString(),
                        ["value"] = initArr[i]?.DeepClone(),
                    });
                }
            }
        }

        if (cellsArr.Count > 0) {
            rowObj["cells"] = cellsArr;
        }

        ApplyStateRowModifiers(
            keyword: "table",
            kind: table.Kind,
            modifiers: table.Modifiers,
            rowName: table.Name,
            rowObj: rowObj,
            scope: scope
        );

        if (
            (rowObj["capacity"] is JsonValue capVal) &&
            capVal.TryGetValue<int>(value: out var capacity) &&
            (capacity < cellsArr.Count)
        ) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationCapacityTooSmall,
                message: $"table '{table.Name}' declares capacity {capacity} smaller than its {cellsArr.Count} authored cells",
                span: table.Span
            );
        }

        // Cells or a capacity already infer a keyed row; only a table with neither needs its domain spelled, or it
        // would be read back as a slot.
        if (
            (cellsArr.Count == 0) &&
            !rowObj.ContainsKey(propertyName: "capacity")
        ) {
            rowObj["domain"] = new JsonObject { ["$type"] = "keys" };
        }

        if ((table.Kind == "Vector") && spaceInfo.HasValue) {
            int? capInt = (((rowObj["capacity"] is JsonValue cv) && cv.TryGetValue<int>(value: out var ci)) ? ci : null);

            ValidateVectorCeilings(table.Name, capInt, cellsArr.Count, spaceInfo.Value.Dimensions, scope, table.Span, ref totalVectorBytes);
        }

        if (table.Kind == "Text") {
            var embedsMod = table.Modifiers.FirstOrDefault(predicate: m => string.Equals(a: m.Name, b: "embeds", comparisonType: StringComparison.OrdinalIgnoreCase));

            if (embedsMod is not null) {
                companionRow = CreateEmbedsCompanionRow(embedsMod: embedsMod, rootObj: rootObj, scope: scope, textRowObj: rowObj, textTable: table, totalVectorBytes: ref totalVectorBytes);
            }
        }

        return rowObj;
    }
    private static JsonObject LowerStateSlotDeclaration(
        StateSlotDeclarationNode slot,
        DocumentScope scope,
        ref long totalVectorBytes
    ) {
        slot = slot with { Kind = ((slot.Enum is null) ? InferSlotKind(scope: scope, slot: slot) : "Int") };
        ValidateStateRowName(
            kind: "slot",
            name: slot.Name,
            scope: scope,
            span: slot.Span
        );
        ValidateStateKind(
            kind: slot.Kind,
            rowName: slot.Name,
            scope: scope,
            span: slot.Span
        );

        var rowObj = new JsonObject {
            ["name"] = slot.Name,
            ["kind"] = slot.Kind,
        };

        if (slot.Enum is { } slotEnum) {
            rowObj["enum"] = slotEnum;
        }

        var rootObj = ((scope.Annotations.TryGetValue(key: "WorldDocumentRoot", value: out var rObj) && (rObj is JsonObject ro)) ? ro : new JsonObject());

        var (spaceName, spaceInfo) = ResolveVectorSpace(
            declarationKind: "slot",
            kind: slot.Kind,
            missingDeclContext: "slot",
            modifiers: slot.Modifiers,
            name: slot.Name,
            rootObj: rootObj,
            rowObj: rowObj,
            scope: scope,
            span: slot.Span
        );

        if (slot.Value is { } value) {
            if (slot.Kind == "Vector") {
                rowObj["value"] = LowerVectorCellValue(
                    context: $"slot '{slot.Name}'",
                    expr: value,
                    scope: scope,
                    spaceName: (spaceName ?? "")
                );
            } else {
                rowObj["value"] = LowerStateScalarValue(
                    context: $"slot '{slot.Name}'",
                    expr: value,
                    kind: slot.Kind,
                    scope: scope
                );
            }
        }

        ApplyStateRowModifiers(
            keyword: "slot",
            kind: slot.Kind,
            modifiers: slot.Modifiers,
            rowName: slot.Name,
            rowObj: rowObj,
            scope: scope
        );

        if ((slot.Kind == "Vector") && spaceInfo.HasValue) {
            ValidateVectorCeilings(slot.Name, capacity: 1, cellCount: 1, spaceInfo.Value.Dimensions, scope, slot.Span, ref totalVectorBytes);
        }

        return rowObj;
    }

    // A cross-row reference a `pile`/`grid` declaration cannot resolve at its own point in `state.world` — the
    // referenced row may be declared earlier or later in the same block. Collected while lowering each declaration
    // and settled once by `ValidateStateCrossReferences` after every row in the block has its own JSON object, so
    // reference order never matters.
    private enum PendingStateReferenceKind {
        PileTokenDomain,
        GridPositions,
        GridInverse,
    }
    private sealed record PendingStateReference(PendingStateReferenceKind Kind, SourceSpan Span, string OwnRowName, string RowName, string? SecondaryRowName = null, int? Capacity = null);

    private static JsonObject LowerStatePileDeclaration(StatePileDeclarationNode pile, DocumentScope scope, List<PendingStateReference> pending) {
        ValidateStateRowName(
            kind: "pile",
            name: pile.Name,
            scope: scope,
            span: pile.Span
        );

        var rowObj = new JsonObject {
            ["name"] = pile.Name,
            ["kind"] = nameof(Puck.State.CellKind.Bool),
            ["domain"] = new JsonObject { ["$type"] = "keysOf", ["row"] = pile.TokenRow, ["ordered"] = true },
        };

        var cellsArr = new JsonArray();
        var seenKeys = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var token in pile.Tokens) {
            if (!seenKeys.Add(item: token.Key)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationDuplicateToken,
                    message: $"pile '{pile.Name}' declares token '{token.Key}' more than once",
                    span: token.Span
                );

                continue;
            }
            if (token.Key.StartsWith(value: '$')) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationReservedKey,
                    message: $"pile '{pile.Name}' token '{token.Key}' carries the reserved '$' prefix — reserved keys are engine-minted",
                    span: token.Span
                );
            }

            cellsArr.Add(item: new JsonObject { ["key"] = token.Key, ["value"] = true });
        }

        if (pile.Initializer is not null) {
            var initVal = DocumentLowering.LowerValue(expr: pile.Initializer, scope: scope);

            if (initVal is JsonArray initArr) {
                foreach (var item in initArr) {
                    var key = (item?.ToString() ?? string.Empty);

                    if (!seenKeys.Add(item: key)) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationDuplicateToken,
                            message: $"pile '{pile.Name}' declares token '{key}' more than once",
                            span: pile.Span
                        );

                        continue;
                    }

                    cellsArr.Add(item: new JsonObject { ["key"] = key, ["value"] = true });
                }
            }
        }

        // A pile's body is its cells, so an empty body lowers to an empty cells array rather than none.
        rowObj["cells"] = cellsArr;

        int? capacity = null;
        var sawCapacity = false;

        foreach (var modifier in pile.Modifiers) {
            if (modifier.Name != "capacity") {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
                    message: $"'{modifier.Name}' is not a modifier 'pile {pile.Name}' admits — expected 'capacity'",
                    span: modifier.Span
                );

                continue;
            }
            if (sawCapacity) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationBehaviorConflict,
                    message: $"pile '{pile.Name}' declares 'capacity' more than once",
                    span: modifier.Span
                );

                continue;
            }

            sawCapacity = true;

            var capNode = LowerStateCapacityModifier(
                modifier: modifier,
                rowName: pile.Name,
                scope: scope
            );

            rowObj["capacity"] = capNode;
            capacity = (((capNode is JsonValue capVal) && capVal.TryGetValue<int>(value: out var cap))
                ? cap
                : null
            );
        }

        if (
            (capacity is { } declaredCapacity) &&
            (declaredCapacity < cellsArr.Count)
        ) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationCapacityTooSmall,
                message: $"pile '{pile.Name}' declares capacity {declaredCapacity} smaller than its {cellsArr.Count} authored tokens",
                span: pile.Span
            );
        }

        pending.Add(item: new PendingStateReference(
            Capacity: capacity,
            Kind: PendingStateReferenceKind.PileTokenDomain,
            OwnRowName: pile.Name,
            RowName: pile.TokenRow,
            Span: pile.Span
        ));

        return rowObj;
    }

    private static readonly HashSet<string> GridModifierNames = new(comparer: StringComparer.Ordinal) { "dimensions", "wrap", "cellSize", "origin", "band", "empty", "positions", "inverse", "bounds" };
    private static readonly HashSet<string> GridWrapNames = new(comparer: StringComparer.Ordinal) { "None", "X", "Y", "Both" };

    private static JsonObject LowerStateGridDeclaration(StateGridDeclarationNode grid, DocumentScope scope, JsonObject stateObj, List<PendingStateReference> pending) {
        grid = grid with { Kind = ((grid.Enum is null) ? InferGridKind(grid: grid, scope: scope) : "Int") };
        ValidateStateRowName(
            kind: "grid",
            name: grid.Name,
            scope: scope,
            span: grid.Span
        );

        if (grid.Kind is not ("Int" or "Bool")) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationKindNotAdmitted,
                message: $"'grid {grid.Name}' names kind '{grid.Kind}' — a grid admits only Int or Bool cells",
                span: grid.Span
            );
        }

        var width = 0L;
        var depth = 0L;
        var sawDimensions = false;
        var wrap = "None";
        var cellSize = 1.0;
        var originX = 0.0;
        var originY = 0.0;
        var originZ = 0.0;
        var band = 0.0;
        var emptyValue = 0L;
        string? positionsRow = null;
        var positionsSpan = grid.Span;
        string? inverseTokens = null;
        string? inverseCodes = null;
        var inverseSpan = grid.Span;
        var sawWrap = false;
        var sawCellSize = false;
        var sawOrigin = false;
        var sawBand = false;
        var sawEmpty = false;
        var sawPositions = false;
        var sawInverse = false;
        var sawBounds = false;
        var rowObj = new JsonObject {
            ["name"] = grid.Name,
            ["kind"] = grid.Kind,
        };

        if (grid.Enum is { } gridEnum) {
            rowObj["enum"] = gridEnum;
        }

        foreach (var modifier in grid.Modifiers) {
            switch (modifier.Name) {
                case "dimensions":
                    if (sawDimensions) {
                        ReportGridModifierRepeated(
                            grid: grid,
                            modifier: modifier,
                            scope: scope
                        );

                        break;
                    }

                    sawDimensions = true;

                    ExpressionNode? widthExpr = null;
                    ExpressionNode? depthExpr = null;

                    foreach (var arg in modifier.Arguments) {
                        switch (arg.Name) {
                            case "width":
                                widthExpr = arg.Value;
                                break;
                            case "depth":
                                depthExpr = arg.Value;
                                break;
                            default:
                                scope.Diagnostics.ReportError(
                                    code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
                                    message: $"'dimensions' on 'grid {grid.Name}' admits only 'width' and 'depth' — not '{(arg.Name ?? "a positional argument")}'",
                                    span: arg.Span
                                );

                                break;
                        }
                    }
                    if (
                        (widthExpr is null) ||
                        !TryResolveStatePositiveWholeNumber(
                        expr: widthExpr,
                        scope: scope,
                        value: out width
                    )
                    ) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                            message: $"'dimensions' on 'grid {grid.Name}' requires a positive whole-number 'width'",
                            span: modifier.Span
                        );
                    }
                    if (
                        (depthExpr is null) ||
                        !TryResolveStatePositiveWholeNumber(
                        expr: depthExpr,
                        scope: scope,
                        value: out depth
                    )
                    ) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                            message: $"'dimensions' on 'grid {grid.Name}' requires a positive whole-number 'depth'",
                            span: modifier.Span
                        );
                    }

                    break;
                case "wrap":
                    if (sawWrap) {
                        ReportGridModifierRepeated(
                            grid: grid,
                            modifier: modifier,
                            scope: scope
                        );

                        break;
                    }

                    sawWrap = true;

                    if (
                        (modifier.Arguments.Count == 1) &&
                        (modifier.Arguments[0].Name is null) &&
                        (modifier.Arguments[0].Value is IdentifierExpressionNode { Name: { } wrapName }) &&
                        GridWrapNames.Contains(item: wrapName)
                    ) {
                        wrap = wrapName;
                    } else {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                            message: $"'wrap' on 'grid {grid.Name}' takes one of 'None', 'X', 'Y', or 'Both'",
                            span: modifier.Span
                        );
                    }

                    break;
                case "cellSize":
                    if (sawCellSize) {
                        ReportGridModifierRepeated(
                            grid: grid,
                            modifier: modifier,
                            scope: scope
                        );

                        break;
                    }

                    sawCellSize = true;

                    if (
                        (modifier.Arguments.Count != 1) ||
                        (modifier.Arguments[0].Name is not null) ||
                        !TryResolveStateFloat(
                        expr: modifier.Arguments[0].Value,
                        scope: scope,
                        value: out cellSize
                    ) ||
                        (cellSize <= 0.0)
                    ) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                            message: $"'cellSize' on 'grid {grid.Name}' takes one positive number",
                            span: modifier.Span
                        );
                    }

                    break;
                case "origin":
                    if (sawOrigin) {
                        ReportGridModifierRepeated(
                            grid: grid,
                            modifier: modifier,
                            scope: scope
                        );

                        break;
                    }

                    sawOrigin = true;

                    if (
                        (modifier.Arguments.Count != 3) ||
                        modifier.Arguments.Any(predicate: a => (a.Name is not null)) ||
                        !TryResolveStateFloat(
                        expr: modifier.Arguments[0].Value,
                        scope: scope,
                        value: out originX
                    ) ||
                        !TryResolveStateFloat(
                        expr: modifier.Arguments[1].Value,
                        scope: scope,
                        value: out originY
                    ) ||
                        !TryResolveStateFloat(
                        expr: modifier.Arguments[2].Value,
                        scope: scope,
                        value: out originZ
                    )
                    ) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                            message: $"'origin' on 'grid {grid.Name}' takes exactly three numbers (x, y, z)",
                            span: modifier.Span
                        );
                    }

                    break;
                case "band":
                    if (sawBand) {
                        ReportGridModifierRepeated(
                            grid: grid,
                            modifier: modifier,
                            scope: scope
                        );

                        break;
                    }

                    sawBand = true;

                    if (
                        (modifier.Arguments.Count != 1) ||
                        (modifier.Arguments[0].Name is not null) ||
                        !TryResolveStateFloat(
                        expr: modifier.Arguments[0].Value,
                        scope: scope,
                        value: out band
                    )
                    ) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                            message: $"'band' on 'grid {grid.Name}' takes one number",
                            span: modifier.Span
                        );
                    }

                    break;
                case "empty":
                    if (sawEmpty) {
                        ReportGridModifierRepeated(
                            grid: grid,
                            modifier: modifier,
                            scope: scope
                        );

                        break;
                    }

                    sawEmpty = true;

                    if (
                        (modifier.Arguments.Count == 1) &&
                        (modifier.Arguments[0].Name is null)
                    ) {
                        emptyValue = LowerStateBoardEmptyValue(
                            context: $"'grid {grid.Name}' empty",
                            expr: modifier.Arguments[0].Value,
                            kind: grid.Kind,
                            scope: scope
                        );
                    } else {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                            message: $"'empty' on 'grid {grid.Name}' takes one value of the grid's own kind",
                            span: modifier.Span
                        );
                    }

                    break;
                case "positions":
                    if (sawPositions) {
                        ReportGridModifierRepeated(
                            grid: grid,
                            modifier: modifier,
                            scope: scope
                        );

                        break;
                    }

                    sawPositions = true;

                    if (
                        (modifier.Arguments.Count == 1) &&
                        (modifier.Arguments[0].Name is null) &&
                        (modifier.Arguments[0].Value is IdentifierExpressionNode { Name: { } positionsName })
                    ) {
                        positionsRow = positionsName;
                        positionsSpan = modifier.Span;
                    } else {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                            message: $"'positions' on 'grid {grid.Name}' takes one row name",
                            span: modifier.Span
                        );
                    }

                    break;
                case "inverse":
                    if (sawInverse) {
                        ReportGridModifierRepeated(
                            grid: grid,
                            modifier: modifier,
                            scope: scope
                        );

                        break;
                    }

                    sawInverse = true;
                    inverseSpan = modifier.Span;

                    foreach (var arg in modifier.Arguments) {
                        switch (arg.Name) {
                            case "tokens" when (arg.Value is IdentifierExpressionNode { Name: { } tokensName }):
                                inverseTokens = tokensName;
                                break;
                            case "codes" when (arg.Value is IdentifierExpressionNode { Name: { } codesName }):
                                inverseCodes = codesName;
                                break;
                            default:
                                scope.Diagnostics.ReportError(
                                    code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
                                    message: $"'inverse' on 'grid {grid.Name}' admits only row-name arguments 'tokens' and 'codes'",
                                    span: arg.Span
                                );

                                break;
                        }
                    }
                    if (
                        (inverseTokens is null) ||
                        (inverseCodes is null)
                    ) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                            message: $"'inverse' on 'grid {grid.Name}' requires both 'tokens' and 'codes'",
                            span: modifier.Span
                        );
                    }

                    break;
                case "bounds":
                    if (sawBounds) {
                        ReportGridModifierRepeated(
                            grid: grid,
                            modifier: modifier,
                            scope: scope
                        );

                        break;
                    }
                    if (!AdmitsKind(
                        enclosing: "world",
                        keyword: "grid",
                        kind: grid.Kind,
                        member: "bounds",
                        position: WorldMemberPosition.Modifier,
                        scope: scope
                    )) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted,
                            message: $"'bounds' is only legal where the row's kind is {KindList(kinds: AdmittedKindsOf(
                                enclosing: "world",
                                keyword: "grid",
                                member: "bounds",
                                position: WorldMemberPosition.Modifier,
                                scope: scope
                            ))} — 'grid {grid.Name}' is {grid.Kind}",
                            span: modifier.Span
                        );

                        break;
                    }

                    sawBounds = true;

                    LowerStateBoundsModifier(
                        kind: grid.Kind,
                        modifier: modifier,
                        rowName: grid.Name,
                        rowObj: rowObj,
                        scope: scope
                    );

                    break;
                default:
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
                        message: ((modifier.Name == "enum")
                            ? EnumModifierRefusal(keyword: "grid", rowName: grid.Name)
                            : $"'{modifier.Name}' is not a modifier 'grid {grid.Name}' admits — expected {string.Join(separator: ", ", values: GridModifierNames.Order(comparer: StringComparer.Ordinal))}"),
                        span: modifier.Span
                    );

                    break;
            }
        }

        if (!sawDimensions) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                message: $"'grid {grid.Name}' requires 'dimensions(width:, depth:)'",
                span: grid.Span
            );
        }

        var domainObj = new JsonObject { ["$type"] = "cellsOf", ["topology"] = grid.Name };

        if (emptyValue != 0L) {
            domainObj["empty"] = JsonValue.Create(value: emptyValue);
        }

        rowObj["domain"] = domainObj;

        var cellsArr = new JsonArray();
        var seenKeys = new HashSet<string>(comparer: StringComparer.Ordinal);
        var cellCeiling = (((width > 0) && (depth > 0))
            ? (width * depth)
            : long.MaxValue);

        foreach (var cell in grid.Cells) {
            if (!seenKeys.Add(item: cell.Key)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationDuplicateToken,
                    message: $"grid '{grid.Name}' declares cell '{cell.Key}' more than once",
                    span: cell.Span
                );

                continue;
            }
            if (
                !long.TryParse(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var ordinal,
                s: cell.Key,
                style: System.Globalization.NumberStyles.Integer
            ) ||
                (ordinal < 0) ||
                (ordinal >= cellCeiling)
            ) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationInvalidCellReference,
                    message: $"grid '{grid.Name}' cell '{cell.Key}' is not a whole-number topology cell ordinal inside 0..{(cellCeiling - 1)}",
                    span: cell.Span
                );

                continue;
            }
            if (cell.Modifiers.Count > 0) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted,
                    message: $"grid '{grid.Name}' cell '{cell.Key}' admits no modifier — a board cell is a literal value",
                    span: cell.Modifiers[0].Span
                );
            }

            cellsArr.Add(item: new JsonObject {
                ["key"] = cell.Key,
                ["value"] = LowerStateScalarValue(
                    context: $"grid '{grid.Name}' cell '{cell.Key}'",
                    expr: cell.Value,
                    kind: grid.Kind,
                    scope: scope
                ),
            });
        }

        if (cellsArr.Count > 0) {
            if (sawInverse) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationInverseWithAuthoredCells,
                    message: $"grid '{grid.Name}' combines 'inverse(...)' with an authored cell body — a derived board's cells come from its tokens/codes rows alone",
                    span: grid.Span
                );
            } else {
                rowObj["cells"] = cellsArr;
            }
        }

        if (sawInverse && (inverseTokens is not null) && (inverseCodes is not null)) {
            rowObj["inverse"] = new JsonObject { ["tokens"] = inverseTokens, ["codes"] = inverseCodes };

            pending.Add(item: new PendingStateReference(
                Kind: PendingStateReferenceKind.GridInverse,
                OwnRowName: grid.Name,
                RowName: inverseTokens,
                SecondaryRowName: inverseCodes,
                Span: inverseSpan
            ));
        }
        if (sawPositions && (positionsRow is not null)) {
            pending.Add(item: new PendingStateReference(
                Kind: PendingStateReferenceKind.GridPositions,
                OwnRowName: grid.Name,
                RowName: positionsRow,
                Span: positionsSpan
            ));
        }

        var lattices = ((stateObj["lattices"] as JsonArray) ?? ((JsonArray)(stateObj["lattices"] = new JsonArray())));
        var duplicateTopology = false;

        foreach (var existingTopology in lattices) {
            if (
                (existingTopology is JsonObject existingObj) &&
                (existingObj["name"] is JsonValue existingNameVal) &&
                existingNameVal.TryGetValue<string>(value: out var existingName) &&
                (existingName == grid.Name)
            ) {
                duplicateTopology = true;

                break;
            }
        }

        if (duplicateTopology) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationDuplicateTopologyName,
                message: $"'state.lattices' already declares a topology named '{grid.Name}'",
                span: grid.Span
            );
        } else {
            var topologyObj = new JsonObject {
                ["$type"] = "grid",
                ["name"] = grid.Name,
                ["origin"] = new JsonArray { CreateNumberNode(value: originX), CreateNumberNode(value: originY), CreateNumberNode(value: originZ) },
                ["cellSize"] = JsonValue.Create(value: cellSize),
                ["width"] = JsonValue.Create(value: width),
                ["depth"] = JsonValue.Create(value: depth),
            };

            if (wrap != "None") {
                topologyObj["wrap"] = wrap;
            }
            if (band != 0.0) {
                topologyObj["band"] = JsonValue.Create(value: band);
            }

            lattices.Add(item: topologyObj);
        }

        return rowObj;
    }
    // A board's `domain.empty` is a raw `long` on the wire (`StateDomain.CellsOf.Empty`) regardless of the row's
    // own `Kind` — unlike a cell's own `value`, it carries no hand-rolled per-kind spelling, so a Bool grid's
    // `true`/`false` literal still lowers to the numeric 1/0 the field's C# type requires.
    private static long LowerStateBoardEmptyValue(ExpressionNode expr, string context, string kind, DocumentScope scope) {
        var literal = ResolveStateLiteral(
            expr: expr,
            scope: scope
        );

        if (
            (kind == "Bool") &&
            (literal is LiteralExpressionNode { Unit: null, Value: bool b })
        ) {
            return (b ? 1L : 0L);
        }
        if (literal is LiteralExpressionNode { Unit: null, Value: long l }) {
            return l;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
            message: $"{context} must be {((kind == "Bool") ? "'true' or 'false'" : "a whole number")} for a {kind} grid",
            span: expr.Span
        );

        return 0L;
    }
    // Declared as `JsonNode` (not the `JsonValue?` `JsonValue.Create` itself returns) so a `new JsonArray { ... }`
    // collection initializer resolves to `JsonArray.Add(JsonNode?)` rather than its generic `Add<T>(T)` overload —
    // an exact-type generic match otherwise wins overload resolution over the non-generic one, and that overload
    // carries the trim/AOT warnings `Puck.World.Transpiler`'s Native AOT-compatible LSP build treats as errors.
    private static JsonNode CreateNumberNode(double value) => JsonValue.Create(value: value)!;
    // A row names the enum its cells are drawn from after its name, so `enum(...)` is refused with that spelling by
    // every declaration that reads modifiers.
    private static string EnumModifierRefusal(string keyword, string rowName) =>
        $"'enum' is not a modifier — a row names the enum its cells are drawn from after its name, as a record field does: '{keyword} {rowName}: Enum'";
    private static void ReportGridModifierRepeated(StateGridDeclarationNode grid, StateModifierNode modifier, DocumentScope scope) {
        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationBehaviorConflict,
            message: $"'grid {grid.Name}' declares '{modifier.Name}' more than once",
            span: modifier.Span
        );
    }
    private static bool TryResolveStatePositiveWholeNumber(ExpressionNode expr, DocumentScope scope, out long value) {
        if (
            TryResolveStateWholeNumber(
            expr: expr,
            scope: scope,
            value: out value
        ) &&
            (value > 0)
        ) {
            return true;
        }

        value = 0;

        return false;
    }
    private static bool TryResolveStateWholeNumber(ExpressionNode expr, DocumentScope scope, out long value) {
        var literal = ResolveStateLiteral(
            expr: expr,
            scope: scope
        );

        switch (literal) {
            case LiteralExpressionNode { Unit: null, Value: long l }:
                value = l;

                return true;
            case LiteralExpressionNode { Unit: null, Value: ulong ul } when (ul <= long.MaxValue):
                value = ((long)ul);

                return true;
            default:
                value = 0;

                return false;
        }
    }
    private static bool TryResolveStateFloat(ExpressionNode expr, DocumentScope scope, out double value) {
        var literal = ResolveStateLiteral(
            expr: expr,
            scope: scope
        );

        switch (literal) {
            case LiteralExpressionNode { Unit: null, Value: long l }:
                value = l;

                return true;
            case LiteralExpressionNode { Unit: null, Value: ulong ul }:
                value = ul;

                return true;
            case LiteralExpressionNode { Unit: null, Value: double d }:
                value = d;

                return true;
            default:
                value = 0.0;

                return false;
        }
    }
    // A pile's `of` and a grid's `positions`/`inverse` name another `state.world` row that may be declared earlier
    // or later in the same block — settled once every row has its own JSON object, so declaration order never
    // matters. Mutates the referenced row's own object in place for `positions` (adding `valuesFrom`); a grid's
    // own `inverse` member is already set at its declaration site and is only checked, not written, here.
    private static void ValidateStateRowName(string kind, string name, DocumentScope scope, SourceSpan span) {
        if (name.StartsWith(value: '$')) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationReservedKey,
                message: $"{kind} name '{name}' carries the reserved '$' prefix — reserved row names are engine-minted",
                span: span
            );
        }
    }
    private static void ValidateStateKind(string kind, string rowName, DocumentScope scope, SourceSpan span) {
        if (!AdmittedRowKinds.Contains(item: kind)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.UnknownKindAnnotation,
                message: $"'{rowName}' names an unrecognized kind '{kind}' — expected {string.Join(
                    separator: ", ",
                    values: AdmittedRowKinds.Order(comparer: StringComparer.Ordinal)
                )}",
                span: span
            );
        }
    }
    private static void ApplyStateRowModifiers(JsonObject rowObj, IReadOnlyList<StateModifierNode> modifiers, string kind, string keyword, string rowName, DocumentScope scope) {
        var admitted = ModifiersOf(
            enclosing: "world",
            keyword: keyword,
            scope: scope
        );
        var slotModifiers = ModifiersOf(
            enclosing: "world",
            keyword: "slot",
            scope: scope
        );
        // The modifiers a table admits and a slot does not: a slot is always exactly one cell, so these get their
        // own refusal rather than the generic one.
        var tableOnly = new HashSet<string>(
            collection: ModifiersOf(
                enclosing: "world",
                keyword: "table",
                scope: scope
            ),
            comparer: StringComparer.Ordinal
        );

        tableOnly.ExceptWith(other: slotModifiers);
        var sawCapacity = false;
        var sawBounds = false;
        var sawAdvance = false;
        var sawEvicts = false;
        var sawSpace = false;

        foreach (var modifier in modifiers) {
            if (!admitted.Contains(item: modifier.Name)) {
                scope.Diagnostics.ReportError(
                    code: ((slotModifiers.Contains(item: modifier.Name) || tableOnly.Contains(item: modifier.Name))
                        ? PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted
                        : PuckDiagnosticCodes.StateDeclarationUnknownModifier
                    ),
                    message: ((modifier.Name == "enum")
                        ? EnumModifierRefusal(keyword: keyword, rowName: rowName)
                        : (tableOnly.Contains(item: modifier.Name)
                        ? $"'{modifier.Name}' is only legal on a 'table' declaration — 'slot {rowName}' is always exactly one cell"
                        : $"'{modifier.Name}' is not a modifier '{rowName}' admits — expected {string.Join(separator: ", ", values: admitted.Order(comparer: StringComparer.Ordinal))}")
                    ),
                    span: modifier.Span
                );

                continue;
            }

            switch (modifier.Name) {
                case "capacity":
                    if (sawCapacity) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationBehaviorConflict,
                            message: $"table '{rowName}' declares 'capacity' more than once",
                            span: modifier.Span
                        );

                        break;
                    }

                    sawCapacity = true;
                    rowObj["capacity"] = LowerStateCapacityModifier(
                        modifier: modifier,
                        rowName: rowName,
                        scope: scope
                    );

                    break;
                case "evicts":
                    if (sawEvicts) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationBehaviorConflict,
                            message: $"table '{rowName}' declares 'evicts' more than once",
                            span: modifier.Span
                        );

                        break;
                    }

                    sawEvicts = true;
                    rowObj["evicts"] = true;
                    break;
                case "space":
                    if (!AdmitsKind(
                        enclosing: "world",
                        keyword: keyword,
                        kind: kind,
                        member: "space",
                        position: WorldMemberPosition.Modifier,
                        scope: scope
                    )) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.EmbeddingSpaceUnknown,
                            message: $"Row '{rowName}' of kind '{kind}' cannot declare 'space' — only a row whose kind is {KindList(kinds: AdmittedKindsOf(
                                enclosing: "world",
                                keyword: keyword,
                                member: "space",
                                position: WorldMemberPosition.Modifier,
                                scope: scope
                            ))} names a space.",
                            span: modifier.Span
                        );
                        break;
                    }
                    if (sawSpace) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationBehaviorConflict,
                            message: $"'{rowName}' declares 'space' more than once",
                            span: modifier.Span
                        );
                        break;
                    }
                    sawSpace = true;
                    string? spaceName = null;
                    if (modifier.Arguments.Count > 0) {
                        if (modifier.Arguments[0].Value is IdentifierExpressionNode idNode) {
                            spaceName = idNode.Name;
                        } else if (modifier.Arguments[0].Value is LiteralExpressionNode { Value: string sVal }) {
                            spaceName = sVal;
                        }
                    }
                    if (string.IsNullOrEmpty(value: spaceName)) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.EmbeddingSpaceInvalid,
                            message: $"Row '{rowName}' declares 'space' without a valid space name.",
                            span: modifier.Span
                        );
                        break;
                    }
                    rowObj["space"] = spaceName;
                    break;
                case "embeds":
                    if (!AdmitsKind(
                        enclosing: "world",
                        keyword: keyword,
                        kind: kind,
                        member: "embeds",
                        position: WorldMemberPosition.Modifier,
                        scope: scope
                    )) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.EmbedsInvalid,
                            message: $"'embeds' is only legal where the row's kind is {KindList(kinds: AdmittedKindsOf(
                                enclosing: "world",
                                keyword: keyword,
                                member: "embeds",
                                position: WorldMemberPosition.Modifier,
                                scope: scope
                            ))} — table '{rowName}' is {kind}.",
                            span: modifier.Span
                        );
                    }
                    break;
                case "bounds":
                    if (!AdmitsKind(
                        enclosing: "world",
                        keyword: keyword,
                        kind: kind,
                        member: "bounds",
                        position: WorldMemberPosition.Modifier,
                        scope: scope
                    )) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted,
                            message: $"'bounds' is only legal where the row's kind is {KindList(kinds: AdmittedKindsOf(
                                enclosing: "world",
                                keyword: keyword,
                                member: "bounds",
                                position: WorldMemberPosition.Modifier,
                                scope: scope
                            ))} — '{rowName}' is {kind}",
                            span: modifier.Span
                        );

                        break;
                    }
                    if (sawBounds) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationBehaviorConflict,
                            message: $"'{rowName}' declares 'bounds' more than once",
                            span: modifier.Span
                        );

                        break;
                    }

                    sawBounds = true;
                    LowerStateBoundsModifier(
                        kind: kind,
                        modifier: modifier,
                        rowName: rowName,
                        rowObj: rowObj,
                        scope: scope
                    );

                    break;
                case "advance":
                    if (!AdmitsKind(
                        enclosing: "world",
                        keyword: keyword,
                        kind: kind,
                        member: "advance",
                        position: WorldMemberPosition.Modifier,
                        scope: scope
                    )) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted,
                            message: $"'advance' is only legal where the row's kind is {KindList(kinds: AdmittedKindsOf(
                                enclosing: "world",
                                keyword: keyword,
                                member: "advance",
                                position: WorldMemberPosition.Modifier,
                                scope: scope
                            ))} — '{rowName}' is {kind}",
                            span: modifier.Span
                        );

                        break;
                    }
                    if (sawAdvance) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationBehaviorConflict,
                            message: $"'{rowName}' declares 'advance' more than once",
                            span: modifier.Span
                        );

                        break;
                    }

                    sawAdvance = true;
                    rowObj["advance"] = LowerStateAdvanceModifier(
                        context: $"'{rowName}' advance",
                        modifier: modifier,
                        scope: scope
                    );

                    break;
                default:
                    // The admission above comes from the construct table, so a modifier described for this
                    // declaration but not lowered here would otherwise be accepted and dropped. A description is
                    // honoured or refused by name, never inert.
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted,
                        message: $"'{modifier.Name}' is described for '{rowName}' but this vocabulary does not lower it",
                        span: modifier.Span
                    );

                    break;
            }
        }

        if (sawEvicts && !sawCapacity) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted,
                message: $"table '{rowName}' declares 'evicts' without 'capacity' — evicts requires capacity",
                span: modifiers.First(predicate: m => (m.Name == "evicts")).Span
            );
        }
    }
    private static JsonNode LowerStateCapacityModifier(StateModifierNode modifier, string rowName, DocumentScope scope) {
        if (
            (modifier.Arguments.Count == 1) &&
            (modifier.Arguments[0].Name is null) &&
            (ResolveStateLiteral(
                expr: modifier.Arguments[0].Value,
                scope: scope
            ) is LiteralExpressionNode { Unit: null, Value: long capacity }) &&
            (capacity > 0) &&
            (capacity <= int.MaxValue)
        ) {
            return JsonValue.Create(value: ((int)capacity))!;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
            message: $"'capacity' on '{rowName}' takes one positive whole-number argument",
            span: modifier.Span
        );

        return JsonValue.Create(value: 1)!;
    }
    private static void LowerStateBoundsModifier(StateModifierNode modifier, JsonObject rowObj, string kind, string rowName, DocumentScope scope) {
        ExpressionNode? minExpr = null;
        ExpressionNode? maxExpr = null;
        ExpressionNode? overflowExpr = null;
        var sawRange = false;

        foreach (var arg in modifier.Arguments) {
            if ((arg.Name is null) && (arg.Value is RangeExpressionNode range) && !sawRange) {
                sawRange = true;
                minExpr = range.Start;
                maxExpr = range.End;
                continue;
            }
            switch (arg.Name) {
                case "overflow":
                    overflowExpr = arg.Value;
                    break;
                default:
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
                        message: $"'bounds' on '{rowName}' admits a 'minimum..maximum' range and optional 'overflow' — not '{(arg.Name ?? "a positional argument")}'",
                        span: arg.Span
                    );

                    break;
            }
        }

        if (!sawRange) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
                message: $"'bounds' on '{rowName}' requires the canonical 'minimum..maximum', 'minimum..', or '..maximum' range spelling",
                span: modifier.Span
            );
        }

        if (minExpr is not null) {
            rowObj["min"] = LowerStateScalarValue(
                context: $"'{rowName}' bounds minimum",
                expr: minExpr,
                kind: kind,
                scope: scope
            );
        }
        if (maxExpr is not null) {
            rowObj["max"] = LowerStateScalarValue(
                context: $"'{rowName}' bounds maximum",
                expr: maxExpr,
                kind: kind,
                scope: scope
            );
        }
        if (overflowExpr is IdentifierExpressionNode { Name: "Refuse" or "Saturate" } overflowId) {
            // "Refuse" is the wire default and is omitted rather than written — matches the explicit row form,
            // which simply would not author the field for the default policy.
            if (overflowId.Name == "Saturate") {
                rowObj["overflow"] = "Saturate";
            }
        } else if (overflowExpr is not null) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                message: $"'{rowName}' bounds overflow must be 'Refuse' or 'Saturate'",
                span: overflowExpr.Span
            );
        }
    }
    private static JsonObject LowerStateAdvanceModifier(StateModifierNode modifier, string context, DocumentScope scope) {
        ExpressionNode? rateExpr = null;

        foreach (var arg in modifier.Arguments) {
            if (arg.Name is "perSecond" or null) {
                if (rateExpr is not null) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
                        message: $"{context} takes one 'perSecond' argument",
                        span: arg.Span
                    );

                    continue;
                }

                rateExpr = arg.Value;
            } else {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
                    message: $"{context} admits only 'perSecond' — not '{arg.Name}'",
                    span: arg.Span
                );
            }
        }

        if (rateExpr is null) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
                message: $"{context} requires a 'perSecond' rate",
                span: modifier.Span
            );

            return new JsonObject { ["perSecondNumerator"] = 0L, ["perSecondDenominator"] = 1L };
        }

        if (!TryReduceRate(
            denominator: out var denominator,
            expr: ResolveStateLiteral(
                expr: rateExpr,
                scope: scope
            ),
            numerator: out var numerator
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationRateInexact,
                message: $"{context}'s rate does not reduce to an exact 64-bit fraction",
                span: rateExpr.Span
            );

            return new JsonObject { ["perSecondNumerator"] = 0L, ["perSecondDenominator"] = 1L };
        }

        return new JsonObject { ["perSecondNumerator"] = numerator, ["perSecondDenominator"] = denominator };
    }
    private static bool LowerStateBehaviorModifier(StateModifierNode modifier, JsonObject cellObj, string context, DocumentScope scope) {
        if (
            (modifier.Arguments.Count == 1) &&
            (modifier.Arguments[0].Name is null) &&
            (modifier.Arguments[0].Value is IdentifierExpressionNode { Name: "none" })
        ) {
            cellObj["behavior"] = nameof(Puck.State.StateCellBehavior.None);

            return true;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
            message: $"{context}'s 'behavior' modifier admits only 'none'",
            span: modifier.Span
        );

        return false;
    }
    // Converts an authored default/bound literal into the exact JSON spelling `StateRowJsonConverter` reads back
    // for the row's kind: a plain number for Int, a 0/1 boolean for Bool, a plain string for Text, and — Fixed's own
    // convention throughout the engine — a decimal STRING (never raw Q48.16 bits) for Fixed.
    // A declaration value or modifier argument may be any compile-time expression, a `let` constant included: it is
    // evaluated once and read back as the literal it produces, so every literal rule below applies unchanged.
    private static (string? SpaceName, EmbeddingIdentity? SpaceIdentity) ResolveVectorSpace(
        string kind,
        IReadOnlyList<StateModifierNode> modifiers,
        string name,
        string declarationKind,
        string missingDeclContext,
        SourceSpan span,
        JsonObject rowObj,
        JsonObject rootObj,
        DocumentScope scope
    ) {
        if (kind != "Vector") {
            return (null, null);
        }

        string? spaceName = null;
        var spaceMod = modifiers.FirstOrDefault(predicate: m => string.Equals(a: m.Name, b: "space", comparisonType: StringComparison.OrdinalIgnoreCase));

        if ((spaceMod is not null) && (spaceMod.Arguments.Count > 0)) {
            if (spaceMod.Arguments[0].Value is IdentifierExpressionNode idNode) {
                spaceName = idNode.Name;
            } else if (spaceMod.Arguments[0].Value is LiteralExpressionNode { Value: string sVal }) {
                spaceName = sVal;
            }
        }

        if (string.IsNullOrEmpty(value: spaceName)) {
            spaceName = FindDefaultSpace(parent: rootObj);
        }

        EmbeddingIdentity? spaceInfo = null;

        if (string.IsNullOrEmpty(value: spaceName)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.EmbeddingSpaceUnknown,
                message: $"Vector {declarationKind} '{name}' has no space and no default space was declared.",
                span: span
            );
        } else {
            rowObj["space"] = spaceName;
            spaceInfo = FindSpaceIdentity(parent: rootObj, spaceName: spaceName);
            if (!spaceInfo.HasValue) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.EmbeddingSpaceUnknown,
                    message: $"Embedding space '{spaceName}' declared on {missingDeclContext} '{name}' was not found in 'spaces'.",
                    span: span
                );
            }
        }

        return (spaceName, spaceInfo);
    }
}
