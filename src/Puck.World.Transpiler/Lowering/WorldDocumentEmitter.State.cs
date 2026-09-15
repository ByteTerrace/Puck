using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Puck.Maths;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

// `state { world { table/slot/row declarations } }` (the concise-authoring surface over StateRow) — see
// src/Puck.World.Transpiler/README.md's syntax-contract section for the exact grammar, lowering, and refusals.
public static partial class WorldDocumentEmitter {
    private static readonly HashSet<string> AdmittedCellKinds = new(comparer: StringComparer.Ordinal) { "Int", "Fixed", "Bool", "Text", "Vector" };
    private static readonly HashSet<string> TableRowModifierNames = new(comparer: StringComparer.Ordinal) { "capacity", "bounds", "advance", "evicts", "space", "embeds" };
    private static readonly HashSet<string> SlotRowModifierNames = new(comparer: StringComparer.Ordinal) { "bounds", "advance", "space" };
    private static readonly HashSet<string> CellModifierNames = new(comparer: StringComparer.Ordinal) { "advance", "behavior" };

    private static void LowerStateSectionBlock(BlockNode block, JsonObject parent, DocumentScope scope) {
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

            if (stmt is PropertyNode { Name: "world" } prop) {
                if (scope.Annotations.ContainsKey("StateWorldDeclarationBlock") ||
                    scope.Annotations.ContainsKey("StateWorldSqlForm")) {
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
    }
    private static void LowerStateWorldBlock(BlockNode block, JsonObject stateObj, DocumentScope scope, string statePointer) {
        if (scope.Annotations.ContainsKey("StateWorldArrayForm") ||
            scope.Annotations.ContainsKey("StateWorldDeclarationBlock") ||
            (stateObj.ContainsKey("world") && !scope.Annotations.ContainsKey("StateWorldSqlForm"))) {
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
        long totalVectorBytes = 0;

        foreach (var (stmt, rowScope) in Expand(
            block.Statements,
            scope
        )) {
            JsonObject? companionRow = null;
            var rowObj = (stmt switch {
                StateTableDeclarationNode table => LowerStateTableDeclaration(
                    scope: rowScope,
                    table: table,
                    totalVectorBytes: ref totalVectorBytes,
                    companionRow: out companionRow
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

            if (companionRow is not null) {
                if (companionRow["name"]?.ToString() is { } compName && !seenNames.Add(compName)) {
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

        var rootObj = scope.Annotations.TryGetValue("WorldDocumentRoot", out var rObj) && rObj is JsonObject ro ? ro : new JsonObject();
        string? spaceName = null;
        (string Name, string Model, string Revision, int Dimensions)? spaceInfo = null;

        if (table.Kind == "Vector") {
            var spaceMod = table.Modifiers.FirstOrDefault(m => string.Equals(m.Name, "space", StringComparison.OrdinalIgnoreCase));
            if (spaceMod is not null && spaceMod.Arguments.Count > 0) {
                if (spaceMod.Arguments[0].Value is IdentifierExpressionNode idNode) {
                    spaceName = idNode.Name;
                } else if (spaceMod.Arguments[0].Value is LiteralExpressionNode { Value: string sVal }) {
                    spaceName = sVal;
                }
            }

            if (string.IsNullOrEmpty(spaceName)) {
                spaceName = FindDefaultSpace(rootObj);
            }

            if (string.IsNullOrEmpty(spaceName)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.EmbeddingSpaceUnknown,
                    message: $"Vector row '{table.Name}' has no space and no default space was declared.",
                    span: table.Span
                );
            } else {
                rowObj["space"] = spaceName;
                spaceInfo = FindSpaceInfo(rootObj, spaceName);
                if (!spaceInfo.HasValue) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.EmbeddingSpaceUnknown,
                        message: $"Embedding space '{spaceName}' declared on table '{table.Name}' was not found in 'spaces'.",
                        span: table.Span
                    );
                }
            }
        }

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
                    rootObj: rootObj,
                    scope: scope,
                    spaceName: spaceName ?? ""
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
                    case "advance" when AdmitsBoundsOrAdvance(kind: table.Kind):
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
                            message: $"'advance' is only legal on an Int or Fixed cell — table '{table.Name}' cell '{cell.Key}' is {table.Kind}",
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

        if (cellsArr.Count > 0) {
            rowObj["cells"] = cellsArr;
        }

        ApplyStateRowModifiers(
            admitCapacity: true,
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

        if (table.Kind == "Vector" && spaceInfo.HasValue) {
            int? capInt = (rowObj["capacity"] is JsonValue cv && cv.TryGetValue<int>(out var ci)) ? ci : null;
            ValidateVectorCeilings(table.Name, capInt, cellsArr.Count, spaceInfo.Value.Dimensions, scope, table.Span, ref totalVectorBytes);
        }

        if (table.Kind == "Text") {
            var embedsMod = table.Modifiers.FirstOrDefault(m => string.Equals(m.Name, "embeds", StringComparison.OrdinalIgnoreCase));
            if (embedsMod is not null) {
                companionRow = CreateEmbedsCompanionRow(table, embedsMod, rowObj, rootObj, scope, ref totalVectorBytes);
            }
        }

        return rowObj;
    }
    private static JsonObject LowerStateSlotDeclaration(
        StateSlotDeclarationNode slot,
        DocumentScope scope,
        ref long totalVectorBytes
    ) {
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

        var rootObj = scope.Annotations.TryGetValue("WorldDocumentRoot", out var rObj) && rObj is JsonObject ro ? ro : new JsonObject();
        string? spaceName = null;
        (string Name, string Model, string Revision, int Dimensions)? spaceInfo = null;

        if (slot.Kind == "Vector") {
            var spaceMod = slot.Modifiers.FirstOrDefault(m => string.Equals(m.Name, "space", StringComparison.OrdinalIgnoreCase));
            if (spaceMod is not null && spaceMod.Arguments.Count > 0) {
                if (spaceMod.Arguments[0].Value is IdentifierExpressionNode idNode) {
                    spaceName = idNode.Name;
                } else if (spaceMod.Arguments[0].Value is LiteralExpressionNode { Value: string sVal }) {
                    spaceName = sVal;
                }
            }

            if (string.IsNullOrEmpty(spaceName)) {
                spaceName = FindDefaultSpace(rootObj);
            }

            if (string.IsNullOrEmpty(spaceName)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.EmbeddingSpaceUnknown,
                    message: $"Vector slot '{slot.Name}' has no space and no default space was declared.",
                    span: slot.Span
                );
            } else {
                rowObj["space"] = spaceName;
                spaceInfo = FindSpaceInfo(rootObj, spaceName);
                if (!spaceInfo.HasValue) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.EmbeddingSpaceUnknown,
                        message: $"Embedding space '{spaceName}' declared on slot '{slot.Name}' was not found in 'spaces'.",
                        span: slot.Span
                    );
                }
            }
        }

        if (slot.Value is { } value) {
            if (slot.Kind == "Vector") {
                rowObj["value"] = LowerVectorCellValue(
                    context: $"slot '{slot.Name}'",
                    expr: value,
                    rootObj: rootObj,
                    scope: scope,
                    spaceName: spaceName ?? ""
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
            admitCapacity: false,
            kind: slot.Kind,
            modifiers: slot.Modifiers,
            rowName: slot.Name,
            rowObj: rowObj,
            scope: scope
        );

        if (slot.Kind == "Vector" && spaceInfo.HasValue) {
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

        long width = 0;
        long depth = 0;
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
                    if (!AdmitsBoundsOrAdvance(kind: grid.Kind)) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted,
                            message: $"'bounds' is only legal on an Int grid — 'grid {grid.Name}' is {grid.Kind}",
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
                        message: $"'{modifier.Name}' is not a modifier 'grid {grid.Name}' admits — expected {string.Join(separator: ", ", values: GridModifierNames.Order(comparer: StringComparer.Ordinal))}",
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
        var cellCeiling = ((width > 0) && (depth > 0))
            ? (width * depth)
            : long.MaxValue;

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
                s: cell.Key,
                result: out var ordinal
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

        var lattices = ((stateObj["lattices"] as JsonArray) ?? (JsonArray)(stateObj["lattices"] = new JsonArray()));
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
    private static void ValidateStateCrossReferences(List<PendingStateReference> pending, JsonArray worldArr, DocumentScope scope) {
        if (pending.Count == 0) {
            return;
        }

        var byName = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal);

        foreach (var rowNode in worldArr) {
            if (
                (rowNode is JsonObject rowObj) &&
                (rowObj["name"] is JsonValue nameVal) &&
                nameVal.TryGetValue<string>(value: out var name)
            ) {
                byName[name] = rowObj;
            }
        }

        foreach (var reference in pending) {
            switch (reference.Kind) {
                case PendingStateReferenceKind.PileTokenDomain:
                    ValidatePileTokenDomainReference(
                        byName: byName,
                        reference: reference,
                        scope: scope
                    );

                    break;
                case PendingStateReferenceKind.GridPositions:
                    ValidateGridPositionsReference(
                        byName: byName,
                        reference: reference,
                        scope: scope
                    );

                    break;
                case PendingStateReferenceKind.GridInverse:
                    ValidateGridInverseReference(
                        byName: byName,
                        reference: reference,
                        scope: scope
                    );

                    break;
            }
        }
    }
    private static bool IsRowKindInt(JsonObject row) => ((row["kind"] is JsonValue kindVal) && kindVal.TryGetValue<string>(value: out var kind) && (kind == "Int"));
    private static bool IsPlainTokenDomainRow(JsonObject row) {
        if (row["domain"] is JsonObject domainObj) {
            return (
                (domainObj["$type"] is JsonValue typeVal) &&
                typeVal.TryGetValue<string>(value: out var type) &&
                (type == "keys")
            );
        }

        // Undeclared domain infers Keys exactly when the row carries a capacity or more than one cell, or one
        // cell under an author-chosen key — StateRow.InferDomain's own rule, restated over the row's raw JSON.
        var hasCapacity = row.ContainsKey(propertyName: "capacity");
        var cells = (row["cells"] as JsonArray);

        if (hasCapacity || (cells is { Count: > 1 })) {
            return true;
        }

        return ((cells is { Count: 1 }) && (((cells[0] as JsonObject)?["key"]?.ToString()) != "$value"));
    }
    private static void ValidatePileTokenDomainReference(PendingStateReference reference, IReadOnlyDictionary<string, JsonObject> byName, DocumentScope scope) {
        if (!byName.TryGetValue(
            key: reference.RowName,
            value: out var domainRow
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: $"pile '{reference.OwnRowName}' names no row '{reference.RowName}'",
                span: reference.Span
            );

            return;
        }
        if (!IsPlainTokenDomainRow(row: domainRow)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationReferenceShapeMismatch,
                message: $"pile '{reference.OwnRowName}' names '{reference.RowName}', which is not a plain token-domain row",
                span: reference.Span
            );

            return;
        }
        if (reference.Capacity is not { } capacity) {
            return;
        }

        // A table's declared cells are only its initial population — more keys are legal up to `capacity`, or
        // unbounded (StateCapacity.MaxCellsPerRow) when no capacity is declared — so only a declared capacity is a
        // real ceiling here; the row's current cell count is never one, and checking against it would refuse a
        // capacity this table is free to grow into.
        var domainCount = (((domainRow["capacity"] is JsonValue capVal) && capVal.TryGetValue<int>(value: out var domainCapacity))
            ? domainCapacity
            : ((int?)null)
        );

        if ((domainCount is { } count) && (capacity > count)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationCapacityExceedsDomain,
                message: $"pile '{reference.OwnRowName}' declares capacity {capacity} greater than its token domain '{reference.RowName}' provides ({count})",
                span: reference.Span
            );
        }
    }
    private static void ValidateGridPositionsReference(PendingStateReference reference, IReadOnlyDictionary<string, JsonObject> byName, DocumentScope scope) {
        if (!byName.TryGetValue(
            key: reference.RowName,
            value: out var positionsRow
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: $"grid '{reference.OwnRowName}' positions names no row '{reference.RowName}'",
                span: reference.Span
            );

            return;
        }

        var domainType = ((positionsRow["domain"] as JsonObject)?["$type"] as JsonValue);

        if (
            !IsRowKindInt(row: positionsRow) ||
            (domainType is null) ||
            !domainType.TryGetValue<string>(value: out var domainTypeName) ||
            (domainTypeName != "keysOf")
        ) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationReferenceShapeMismatch,
                message: $"grid '{reference.OwnRowName}' positions names '{reference.RowName}', which is not an integer keysOf row",
                span: reference.Span
            );

            return;
        }

        positionsRow["valuesFrom"] = reference.OwnRowName;
    }
    private static void ValidateGridInverseReference(PendingStateReference reference, IReadOnlyDictionary<string, JsonObject> byName, DocumentScope scope) {
        if (!byName.TryGetValue(
            key: reference.RowName,
            value: out var tokensRow
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: $"grid '{reference.OwnRowName}' inverse.tokens names no row '{reference.RowName}'",
                span: reference.Span
            );

            return;
        }
        if (!byName.TryGetValue(
            key: reference.SecondaryRowName!,
            value: out var codesRow
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: $"grid '{reference.OwnRowName}' inverse.codes names no row '{reference.SecondaryRowName}'",
                span: reference.Span
            );

            return;
        }
        if (
            !IsRowKindInt(row: tokensRow) ||
            !IsPlainTokenDomainRow(row: tokensRow)
        ) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationReferenceShapeMismatch,
                message: $"grid '{reference.OwnRowName}' inverse.tokens '{reference.RowName}' names no keyed integer row",
                span: reference.Span
            );
        }
        if (!IsRowKindInt(row: codesRow)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationReferenceShapeMismatch,
                message: $"grid '{reference.OwnRowName}' inverse.codes '{reference.SecondaryRowName}' names no integer row",
                span: reference.Span
            );
        }

        var tokenCells = ((tokensRow["cells"] as JsonArray) ?? []);
        var codeCells = ((codesRow["cells"] as JsonArray) ?? []);
        var sameShape = (tokenCells.Count == codeCells.Count);

        for (var index = 0; (sameShape && (index < tokenCells.Count)); index++) {
            sameShape = ((((tokenCells[index] as JsonObject)?["key"])?.ToString()) == (((codeCells[index] as JsonObject)?["key"])?.ToString()));
        }
        if (!sameShape) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationReferenceShapeMismatch,
                message: $"grid '{reference.OwnRowName}' inverse.codes '{reference.SecondaryRowName}' must carry the same keys, in the same order, as inverse.tokens '{reference.RowName}'",
                span: reference.Span
            );
        }
    }
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
        if (!AdmittedCellKinds.Contains(item: kind)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.UnknownKindAnnotation,
                message: $"'{rowName}' names an unrecognized kind '{kind}' — expected Int, Fixed, Bool, Text, or Vector",
                span: span
            );
        }
    }
    private static bool AdmitsBoundsOrAdvance(string kind) => (kind is "Int" or "Fixed");
    private static void ApplyStateRowModifiers(JsonObject rowObj, IReadOnlyList<StateModifierNode> modifiers, string kind, string rowName, DocumentScope scope, bool admitCapacity) {
        var admitted = (admitCapacity
            ? TableRowModifierNames
            : SlotRowModifierNames
        );
        var sawCapacity = false;
        var sawBounds = false;
        var sawAdvance = false;
        var sawEvicts = false;
        var sawSpace = false;

        foreach (var modifier in modifiers) {
            if (!admitted.Contains(item: modifier.Name)) {
                scope.Diagnostics.ReportError(
                    code: ((SlotRowModifierNames.Contains(item: modifier.Name) || (modifier.Name == "capacity") || (modifier.Name == "evicts") || (modifier.Name == "embeds"))
                        ? PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted
                        : PuckDiagnosticCodes.StateDeclarationUnknownModifier
                    ),
                    message: ((modifier.Name == "capacity" || modifier.Name == "evicts" || modifier.Name == "embeds")
                        ? $"'{modifier.Name}' is only legal on a 'table' declaration — 'slot {rowName}' is always exactly one cell"
                        : $"'{modifier.Name}' is not a modifier '{rowName}' admits — expected {string.Join(separator: ", ", values: admitted.Order(comparer: StringComparer.Ordinal))}"
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
                    if (kind != "Vector") {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.EmbeddingSpaceUnknown,
                            message: $"Row '{rowName}' of kind '{kind}' cannot declare 'space' — only Vector rows name a space.",
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
                    if (string.IsNullOrEmpty(spaceName)) {
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
                    if (kind != "Text") {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.EmbedsInvalid,
                            message: $"'embeds' is only legal on a Text table — table '{rowName}' is {kind}.",
                            span: modifier.Span
                        );
                    }
                    break;
                case "bounds":
                    if (!AdmitsBoundsOrAdvance(kind: kind)) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted,
                            message: $"'bounds' is only legal on an Int or Fixed row — '{rowName}' is {kind}",
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
                    if (!AdmitsBoundsOrAdvance(kind: kind)) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted,
                            message: $"'advance' is only legal on an Int or Fixed row — '{rowName}' is {kind}",
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
            }
        }

        if (sawEvicts && !sawCapacity) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted,
                message: $"table '{rowName}' declares 'evicts' without 'capacity' — evicts requires capacity",
                span: modifiers.First(m => m.Name == "evicts").Span
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

        foreach (var arg in modifier.Arguments) {
            switch (arg.Name) {
                case "minimum":
                    minExpr = arg.Value;
                    break;
                case "maximum":
                    maxExpr = arg.Value;
                    break;
                case "overflow":
                    overflowExpr = arg.Value;
                    break;
                default:
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationUnknownModifier,
                        message: $"'bounds' on '{rowName}' admits only 'minimum', 'maximum', and 'overflow' — not '{(arg.Name ?? "a positional argument")}'",
                        span: arg.Span
                    );

                    break;
            }
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
    private static ExpressionNode ResolveStateLiteral(ExpressionNode expr, DocumentScope scope) {
        if (expr is LiteralExpressionNode) {
            return expr;
        }
        if (DocumentLowering.LowerValue(
            expr: expr,
            scope: scope
        ) is not JsonValue lowered) {
            return expr;
        }

        object? value = null;

        if (lowered.TryGetValue<long>(value: out var whole)) {
            value = whole;
        } else if (lowered.TryGetValue<bool>(value: out var flag)) {
            value = flag;
        } else if (lowered.TryGetValue<string>(value: out var text)) {
            value = text;
        } else if (lowered.TryGetValue<decimal>(value: out var exact)) {
            value = (((exact == decimal.Truncate(d: exact)) && (exact >= long.MinValue) && (exact <= long.MaxValue))
                ? ((object)((long)exact))
                : ((double)exact)
            );
        } else if (lowered.TryGetValue<double>(value: out var real)) {
            value = real;
        }

        return ((value is null)
            ? expr
            : new LiteralExpressionNode(
                Column: expr.Column,
                Length: expr.Length,
                Line: expr.Line,
                Offset: expr.Offset,
                Value: value
            )
        );
    }
    private static JsonNode LowerStateScalarValue(ExpressionNode expr, string kind, string context, DocumentScope scope) {
        var literal = ResolveStateLiteral(
            expr: expr,
            scope: scope
        );

        return kind switch {
            "Bool" => LowerStateBoolValue(context: context, expr: literal, scope: scope),
            "Text" => LowerStateTextValue(context: context, expr: literal, scope: scope),
            "Fixed" => LowerStateFixedValue(context: context, expr: literal, scope: scope),
            _ => LowerStateIntValue(context: context, expr: literal, scope: scope),
        };
    }
    private static JsonNode LowerStateIntValue(ExpressionNode expr, string context, DocumentScope scope) {
        if (expr is LiteralExpressionNode { Unit: null, Value: long l }) {
            return JsonValue.Create(value: l)!;
        }
        if (
            (expr is LiteralExpressionNode { Unit: null, Value: ulong ul }) &&
            (ul <= long.MaxValue)
        ) {
            return JsonValue.Create(value: ((long)ul))!;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
            message: $"{context} must be a whole number for an Int row",
            span: expr.Span
        );

        return JsonValue.Create(value: 0L)!;
    }
    private static JsonNode LowerStateBoolValue(ExpressionNode expr, string context, DocumentScope scope) {
        if (expr is LiteralExpressionNode { Unit: null, Value: bool b }) {
            return JsonValue.Create(value: b)!;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
            message: $"{context} must be 'true' or 'false' for a Bool row",
            span: expr.Span
        );

        return JsonValue.Create(value: false)!;
    }
    private static JsonNode LowerStateTextValue(ExpressionNode expr, string context, DocumentScope scope) {
        if (expr is LiteralExpressionNode { Unit: null, Value: string s }) {
            return JsonValue.Create(value: s)!;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
            message: $"{context} must be a string for a Text row",
            span: expr.Span
        );

        return JsonValue.Create(value: "")!;
    }
    private static JsonNode LowerStateFixedValue(ExpressionNode expr, string context, DocumentScope scope) {
        if (TryFormatFixedLiteral(
            expr: expr,
            text: out var text
        )) {
            return JsonValue.Create(value: text)!;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
            message: $"{context} must be a decimal number for a Fixed row",
            span: expr.Span
        );

        return JsonValue.Create(value: "0")!;
    }
    private static bool TryFormatFixedLiteral(ExpressionNode expr, out string text) {
        text = "";

        if (expr is not LiteralExpressionNode { Unit: null, Value: var raw }) {
            return false;
        }

        var decimalText = (raw switch {
            long l => l.ToString(provider: CultureInfo.InvariantCulture),
            ulong ul => ul.ToString(provider: CultureInfo.InvariantCulture),
            double d => d.ToString(format: "R", provider: CultureInfo.InvariantCulture),
            _ => null,
        });

        if (
            (decimalText is null) ||
            !FixedQ4816.TryParse(
            provider: CultureInfo.InvariantCulture,
            result: out var parsed,
            s: decimalText
        )
        ) {
            return false;
        }

        text = parsed.ToString();

        return true;
    }
    // A decimal `perSecond` rate reduces to an exact fraction from the author's own digits, never from a double's
    // raw bits: a directly authored literal carries its exact source text on RawText (set by the lexer before it
    // ever rounds that text into a double). Only a value with none — an identifier or expression the lowering
    // pipeline already folded through `double` arithmetic before this reduction ever sees it — falls back to that
    // double's own shortest round-trip text, since no more precise source exists once the value has actually been
    // computed in `double`.
    private static bool TryReduceRate(ExpressionNode expr, out long numerator, out long denominator) {
        numerator = 0L;
        denominator = 1L;

        if (expr is not LiteralExpressionNode { Unit: null, Value: var raw } literal) {
            return false;
        }

        switch (raw) {
            case long l:
                numerator = l;
                denominator = 1L;

                return true;
            case ulong ul when (ul <= long.MaxValue):
                numerator = ((long)ul);
                denominator = 1L;

                return true;
            case double d:
                return TryReduceDecimalRate(
                    denominator: out denominator,
                    numerator: out numerator,
                    text: literal.RawText ?? d.ToString(format: "R", provider: CultureInfo.InvariantCulture)
                );
            default:
                return false;
        }
    }
    // Parses `text` (a sign, digits, an optional '.', and an optional exponent — exactly what the lexer or
    // decimal.ToString can produce) into an exact unscaled BigInteger and a base-10 scale with no intermediate
    // double or decimal, so a rate with more significant digits than either type holds still reduces from the
    // author's own digits instead of silently rounding.
    private static bool TryParseExactDecimalText(string text, out BigInteger unscaled, out int scale) {
        unscaled = BigInteger.Zero;
        scale = 0;

        var mantissa = text;
        var negative = false;

        if ((mantissa.Length > 0) && (mantissa[0] is '+' or '-')) {
            negative = (mantissa[0] == '-');
            mantissa = mantissa[1..];
        }

        var exponent = 0;
        var exponentIndex = mantissa.IndexOfAny(['e', 'E']);

        if (exponentIndex >= 0) {
            if (!int.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out exponent,
                s: mantissa[(exponentIndex + 1)..],
                style: NumberStyles.AllowLeadingSign
            )) {
                return false;
            }

            mantissa = mantissa[..exponentIndex];
        }

        var pointIndex = mantissa.IndexOf('.');
        var digits = ((pointIndex < 0) ? mantissa : (mantissa[..pointIndex] + mantissa[(pointIndex + 1)..]));
        var fractionLength = ((pointIndex < 0) ? 0 : (mantissa.Length - pointIndex - 1));

        if (digits.Length == 0) {
            return false;
        }
        foreach (var c in digits) {
            if (!char.IsAsciiDigit(c)) {
                return false;
            }
        }

        unscaled = BigInteger.Parse(
            provider: CultureInfo.InvariantCulture,
            value: digits
        );
        scale = (fractionLength - exponent);

        if (scale < 0) {
            unscaled *= BigInteger.Pow(
                exponent: -scale,
                value: 10
            );
            scale = 0;
        }
        if (negative) {
            unscaled = -unscaled;
        }

        return true;
    }
    private static bool TryReduceDecimalRate(string text, out long numerator, out long denominator) {
        numerator = 0L;
        denominator = 1L;

        if (!TryParseExactDecimalText(
            scale: out var scale,
            text: text,
            unscaled: out var unscaled
        )) {
            return false;
        }

        var scaledDenominator = BigInteger.Pow(
            exponent: scale,
            value: 10
        );
        var gcd = BigInteger.GreatestCommonDivisor(
            left: BigInteger.Abs(value: unscaled),
            right: scaledDenominator
        );

        if (gcd > BigInteger.Zero) {
            unscaled /= gcd;
            scaledDenominator /= gcd;
        }
        if (
            (unscaled < long.MinValue) ||
            (unscaled > long.MaxValue) ||
            (scaledDenominator > long.MaxValue)
        ) {
            return false;
        }

        numerator = ((long)unscaled);
        denominator = ((long)scaledDenominator);

        return true;
    }
}
