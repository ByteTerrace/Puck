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
    private static readonly HashSet<string> AdmittedCellKinds = new(comparer: StringComparer.Ordinal) { "Int", "Fixed", "Bool", "Text" };
    private static readonly HashSet<string> TableRowModifierNames = new(comparer: StringComparer.Ordinal) { "capacity", "bounds", "advance" };
    private static readonly HashSet<string> SlotRowModifierNames = new(comparer: StringComparer.Ordinal) { "bounds", "advance" };
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

        foreach (var stmt in block.Statements) {
            if (stmt is BlockNode { Identifier: "world", Name: null, Target: null } worldBlock) {
                LowerStateWorldBlock(
                    block: worldBlock,
                    scope: scope,
                    stateObj: stateObj,
                    statePointer: statePointer
                );

                continue;
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
        if (stateObj.ContainsKey(propertyName: "world")) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateWorldSectionMixed,
                message: "'state.world' is authored more than once — write it either as the array form ('world [ ]') or the declaration block ('world { }'), never both",
                span: block.Span
            );

            return;
        }

        var worldArr = new JsonArray();

        stateObj["world"] = worldArr;

        var worldPointer = $"{statePointer}/world";

        scope.SourceMap?.Register(
            jsonPointer: worldPointer,
            span: block.Span
        );

        var seenNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var (stmt, rowScope) in Expand(
            block.Statements,
            scope
        )) {
            var rowObj = (stmt switch {
                StateTableDeclarationNode table => LowerStateTableDeclaration(
                    scope: rowScope,
                    table: table
                ),
                StateSlotDeclarationNode slot => LowerStateSlotDeclaration(
                    scope: rowScope,
                    slot: slot
                ),
                BlockNode { Identifier: "row", Name: null, Target: null } rowBlock => LowerBlockToObject(
                    block: rowBlock,
                    scope: rowScope
                ),
                _ => null,
            });

            if (rowObj is null) {
                ReportUnrecognizedSectionStatement(
                    admitted: "'table', 'slot', and 'row' declarations",
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
        }
    }
    private static JsonObject LowerStateTableDeclaration(StateTableDeclarationNode table, DocumentScope scope) {
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
            // A table always declares its own domain, so an empty or single-cell table is never inferred to be a
            // slot — see the syntax contract's "Defaults" section.
            ["domain"] = new JsonObject { ["$type"] = "keys" },
        };

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

            cellObj["value"] = LowerStateScalarValue(
                context: $"table '{table.Name}' cell '{cell.Key}'",
                expr: cell.Value,
                kind: table.Kind,
                scope: scope
            );

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

        return rowObj;
    }
    private static JsonObject LowerStateSlotDeclaration(StateSlotDeclarationNode slot, DocumentScope scope) {
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

        if (slot.Value is { } value) {
            rowObj["value"] = LowerStateScalarValue(
                context: $"slot '{slot.Name}'",
                expr: value,
                kind: slot.Kind,
                scope: scope
            );
        }

        ApplyStateRowModifiers(
            admitCapacity: false,
            kind: slot.Kind,
            modifiers: slot.Modifiers,
            rowName: slot.Name,
            rowObj: rowObj,
            scope: scope
        );

        return rowObj;
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
                message: $"'{rowName}' names an unrecognized kind '{kind}' — expected Int, Fixed, Bool, or Text",
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

        foreach (var modifier in modifiers) {
            if (!admitted.Contains(item: modifier.Name)) {
                scope.Diagnostics.ReportError(
                    code: ((SlotRowModifierNames.Contains(item: modifier.Name) || (modifier.Name == "capacity"))
                        ? PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted
                        : PuckDiagnosticCodes.StateDeclarationUnknownModifier
                    ),
                    message: ((modifier.Name == "capacity")
                        ? $"'capacity' is only legal on a 'table' declaration — 'slot {rowName}' is always exactly one cell"
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
    }
    private static JsonNode LowerStateCapacityModifier(StateModifierNode modifier, string rowName, DocumentScope scope) {
        if (
            (modifier.Arguments.Count == 1) &&
            (modifier.Arguments[0].Name is null) &&
            (modifier.Arguments[0].Value is LiteralExpressionNode { Unit: null, Value: long capacity }) &&
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
            expr: rateExpr,
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
            cellObj["behavior"] = "none";

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
    private static JsonNode LowerStateScalarValue(ExpressionNode expr, string kind, string context, DocumentScope scope) => kind switch {
        "Bool" => LowerStateBoolValue(context: context, expr: expr, scope: scope),
        "Text" => LowerStateTextValue(context: context, expr: expr, scope: scope),
        "Fixed" => LowerStateFixedValue(context: context, expr: expr, scope: scope),
        _ => LowerStateIntValue(context: context, expr: expr, scope: scope),
    };
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
    // A decimal `perSecond` rate reduces to an exact fraction only through decimal's own base-10 arithmetic — a
    // double's binary representation cannot be trusted to carry the author's intended digits, so this reads the
    // literal's shortest round-trip decimal text and reduces THAT exactly, never the double's raw bit pattern.
    private static bool TryReduceRate(ExpressionNode expr, out long numerator, out long denominator) {
        numerator = 0L;
        denominator = 1L;

        if (expr is not LiteralExpressionNode { Unit: null, Value: var raw }) {
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
                    d: d,
                    denominator: out denominator,
                    numerator: out numerator
                );
            default:
                return false;
        }
    }
    private static bool TryReduceDecimalRate(double d, out long numerator, out long denominator) {
        numerator = 0L;
        denominator = 1L;

        var text = d.ToString(format: "R", provider: CultureInfo.InvariantCulture);

        if (!decimal.TryParse(
            provider: CultureInfo.InvariantCulture,
            result: out var value,
            s: text,
            style: NumberStyles.Float
        )) {
            return false;
        }

        var bits = decimal.GetBits(d: value);
        var scale = (bits[3] >> 16) & 0x7F;
        var negative = ((bits[3] & unchecked((int)0x80000000)) != 0);
        var unscaled = (((BigInteger)((uint)bits[2])) << 64) | (((BigInteger)((uint)bits[1])) << 32) | ((uint)bits[0]);
        var scaledDenominator = BigInteger.Pow(
            exponent: scale,
            value: 10
        );
        var gcd = BigInteger.GreatestCommonDivisor(
            left: unscaled,
            right: scaledDenominator
        );

        if (gcd > BigInteger.Zero) {
            unscaled /= gcd;
            scaledDenominator /= gcd;
        }
        if (negative) {
            unscaled = -unscaled;
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
