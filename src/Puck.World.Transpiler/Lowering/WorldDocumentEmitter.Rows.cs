using System.Text.Json.Nodes;
using Puck.SignedDistance;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Lowering;

// `shape Type "name" { }` (§4.1, a creation-document row — CreationDocument.Shapes, the live successor to the dead
// `solid`/`"solids"` collector this renames in place) and `placements { policy { } placement "id" { } }` (§4.2).
public static partial class WorldDocumentEmitter {
    private static bool LowerShapeBlock(BlockNode block, JsonObject parent, DocumentScope scope) {
        if (parent["shapes"] is not JsonArray shapesArr) {
            shapesArr = [];
            parent["shapes"] = shapesArr;
        }

        // The shared block grammar puts a lone identifier before '{' into Name (no target); with a second one, the
        // first is Target and the second Name. `shape` always carries a type, optionally a name, so the type is
        // whichever slot the lone-identifier case fills and the name is Name only when Target is also present.
        var resolvedName = DocumentLowering.ResolveBlockName(
            block: block,
            scope: scope
        );
        var type = (block.Target ?? resolvedName);
        var shapeName = ((block.Target is not null)
            ? resolvedName
            : null
        );

        var shapeIdx = shapesArr.Count;
        var shapePointer = $"{scope.CurrentPointer}/shapes/{shapeIdx}";

        scope.SourceMap?.Register(
            jsonPointer: shapePointer,
            span: block.Span
        );

        var oldPointer = scope.CurrentPointer;

        scope.CurrentPointer = shapePointer;
        var shapeObj = LowerBlockToObject(
            block: block,
            scope: scope
        );

        scope.CurrentPointer = oldPointer;

        if (type is not null) {
            if (!Enum.TryParse<SdfSolidPrimitive>(
                type,
                ignoreCase: false,
                out _
            )) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.UnknownShapeType,
                    message: $"'{type}' is not a recognized shape type",
                    span: block.Span
                );
            }
            shapeObj["type"] = type;
        }
        if (shapeName is not null) {
            shapeObj["name"] = shapeName;
        }

        foreach (var key in WorldDocumentRowDefaults.ShapeKeys) {
            if (!shapeObj.ContainsKey(propertyName: key)) {
                shapeObj[key] = WorldDocumentRowDefaults.ShapeDefault(
                    index: shapeIdx,
                    key: key
                );
            }
        }

        shapesArr.AppendNode(item: shapeObj);

        return true;
    }
    private static bool LowerPlacementsBlock(BlockNode block, JsonObject parent, DocumentScope scope) {
        if (parent["placements"] is not JsonObject placementsObj) {
            placementsObj = [];
            parent["placements"] = placementsObj;
        }
        if (placementsObj["rows"] is not JsonArray rowsArr) {
            rowsArr = [];
            placementsObj["rows"] = rowsArr;
        }

        var placementsPointer = $"{scope.CurrentPointer}/placements";
        var oldPointer = scope.CurrentPointer;

        scope.SourceMap?.Register(
            jsonPointer: placementsPointer,
            span: block.Span
        );

        foreach (var (stmt, rowScope) in Expand(
            block.Statements,
            scope
        )) {
            if (stmt is BlockNode { Identifier: "placement" } row) {
                var rowIdx = rowsArr.Count;

                rowScope.CurrentPointer = $"{placementsPointer}/rows/{rowIdx}";
                rowScope.SourceMap?.Register(
                    jsonPointer: rowScope.CurrentPointer,
                    span: row.Span
                );
                rowsArr.AppendNode(item: LowerPlacementRow(
                    row: row,
                    scope: rowScope
                ));
            } else if (stmt is PropertyNode prop) {
                rowScope.CurrentPointer = placementsPointer;
                DocumentLowering.AssignOrExtend(
                    placementsObj,
                    prop.Name,
                    LowerExpression(
                        prop.Value,
                        rowScope,
                        prop.Name
                    )
                );
            } else if (stmt is BlockNode { Name: null, Target: null, NameExpression: null } nested) {
                // An object-valued field of the section, written in block form: `policy { }` is the section's own
                // `policy` object, the same field the property spelling fills.
                rowScope.CurrentPointer = $"{placementsPointer}/{nested.Identifier}";
                rowScope.SourceMap?.Register(
                    jsonPointer: rowScope.CurrentPointer,
                    span: nested.Span
                );
                placementsObj[nested.Identifier] = LowerBlockToObject(
                    block: nested,
                    scope: rowScope
                );
            } else {
                ReportUnrecognizedSectionStatement(
                    admitted: "'placement' rows, nested field blocks and plain properties",
                    scope: rowScope,
                    section: "placements",
                    stmt: stmt
                );
            }
        }

        scope.CurrentPointer = oldPointer;

        return true;
    }
    // A section whose rows are read by a dedicated dispatcher still admits `for` and a template invocation: both
    // are flattened to the statements they produce before the dispatcher sees them, so a generated row is
    // indistinguishable from a written one. Flattening RECURSES, so a template may contain a loop, a loop may
    // invoke a template, and a template may invoke another — the dispatcher never learns that any of it happened.
    private static IEnumerable<(StatementNode Statement, DocumentScope Scope)> Expand(IReadOnlyList<StatementNode> statements, DocumentScope scope) {
        foreach (var stmt in statements) {
            foreach (var produced in ExpandOne(
                depth: 0,
                scope: scope,
                statement: stmt
            )) {
                yield return produced;
            }
        }
    }

    // A template that invokes itself would otherwise expand forever; the depth ceiling turns that into a refusal
    // naming the row, which is what an author needs to see.
    private const int MaxExpansionDepth = 32;

    private static IEnumerable<(StatementNode Statement, DocumentScope Scope)> ExpandOne(StatementNode statement, DocumentScope scope, int depth) {
        if (depth > MaxExpansionDepth) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.TemplateExpansionTooDeep,
                message: $"a template or 'for' nested past {MaxExpansionDepth} levels — a template that invokes itself never finishes expanding",
                span: statement.Span
            );

            yield break;
        }

        switch (statement) {
            // The parser reported the statement it could not read where it read it; no section reports it again.
            case ErrorStatementNode:
                yield break;

            case ForStatementNode loop:
                foreach (var (produced, iteration) in DocumentLowering.ExpandForStatements(
                    loop: loop,
                    scope: scope
                )) {
                    foreach (var nested in ExpandOne(
                        depth: (depth + 1),
                        scope: iteration,
                        statement: produced
                    )) {
                        yield return nested;
                    }
                }

                break;

            case ExpressionStatementNode { Expression: CallExpressionNode call } when scope.Templates.ContainsKey(key: call.Name):
                foreach (var (produced, invocation) in DocumentLowering.ExpandTemplateStatements(
                    call: call,
                    scope: scope
                )) {
                    foreach (var nested in ExpandOne(
                        depth: (depth + 1),
                        scope: invocation,
                        statement: produced
                    )) {
                        yield return nested;
                    }
                }

                break;

            case StateTableDeclarationNode table when IsFamily(members: table.FamilyMembers, size: table.FamilySize):
                foreach (var singleTable in ExpandTableFamily(scope: scope, table: table)) {
                    yield return (singleTable, scope);
                }

                break;

            case StateSlotDeclarationNode slot when IsFamily(members: slot.FamilyMembers, size: slot.FamilySize):
                foreach (var singleSlot in ExpandSlotFamily(scope: scope, slot: slot)) {
                    yield return (singleSlot, scope);
                }

                break;

            case StatePileDeclarationNode pile when IsFamily(members: pile.FamilyMembers, size: pile.FamilySize):
                foreach (var singlePile in ExpandPileFamily(pile: pile, scope: scope)) {
                    yield return (singlePile, scope);
                }

                break;

            case StateGridDeclarationNode grid when IsFamily(members: grid.FamilyMembers, size: grid.FamilySize):
                foreach (var singleGrid in ExpandGridFamily(grid: grid, scope: scope)) {
                    yield return (singleGrid, scope);
                }

                break;

            default:
                yield return (statement, scope);

                break;
        }
    }
    // A statement the section's grammar has no slot for would otherwise be dropped without a trace — a one-letter
    // typo in the row keyword silently loses a whole row and everything nested in it.
    private static void ReportUnrecognizedSectionStatement(string section, string admitted, StatementNode stmt, DocumentScope scope) {
        var spelling = stmt switch {
            BlockNode block => $"'{block.Identifier}' block",
            PropertyNode property => $"'{property.Name}' property",
            _ => "statement",
        };

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.UnrecognizedSectionStatement,
            message: $"'{section}' carries no {spelling} — it admits {admitted}",
            span: stmt.Span
        );
    }
    private static JsonObject LowerPlacementRow(BlockNode row, DocumentScope scope) {
        var rowObj = LowerBlockToObject(
            block: row,
            scope: scope
        );

        // Deviates from the addon/shape precedent of mapping a block's quoted name to "name": WorldPlacement's
        // identity field is literally Id.
        if (
            !rowObj.ContainsKey(propertyName: "id") &&
            (DocumentLowering.ResolveBlockName(
            block: row,
            scope: scope
        ) is { Length: > 0 } rowId)
        ) {
            rowObj["id"] = rowId;
        }

        if (rowObj["prototype"] is { } prototypeNode) {
            rowObj.Remove(propertyName: "prototype");
            if (!rowObj.ContainsKey(propertyName: "prototypeId")) {
                rowObj["prototypeId"] = prototypeNode.DeepClone();
            }
        }

        // The bare `solid` flag (PropertyNode never carries it — FlagStatementNode has no ProcessStatement case, so
        // it never reached rowObj through LowerBlockToObject) stands in for `solid: { margin: 0 }`. PUCK028 already
        // refused authoring both spellings on the same row; the explicit object wins when it did.
        var sawBareSolid = row.Statements.Any(predicate: static s => (s is FlagStatementNode { Name: "solid" }));

        if (
            sawBareSolid &&
            !rowObj.ContainsKey(propertyName: "solid")
        ) {
            rowObj["solid"] = WorldDocumentRowDefaults.BareSolid();
        }

        // Only a row carrying its own prototypeId is a whole placement; one without it is either a basis-merge
        // directive or a partial row a basis chain completes, and filling a default there would override the value
        // composition was about to supply. The decompiler elides these two fields on exactly the same condition.
        if (rowObj.ContainsKey(propertyName: "prototypeId")) {
            foreach (var key in WorldDocumentRowDefaults.PlacementKeys) {
                if (!rowObj.ContainsKey(propertyName: key)) {
                    rowObj[key] = WorldDocumentRowDefaults.PlacementDefault(key: key);
                }
            }
        }

        return rowObj;
    }
    private static bool LowerPrototypesBlock(BlockNode block, JsonObject parent, DocumentScope scope) {
        if (parent["prototypes"] is not JsonArray protoArr) {
            protoArr = [];
            parent["prototypes"] = protoArr;
        }

        var prototypesPointer = $"{scope.CurrentPointer}/prototypes";
        var oldPointer = scope.CurrentPointer;

        scope.SourceMap?.Register(
            jsonPointer: prototypesPointer,
            span: block.Span
        );

        foreach (var (stmt, rowScope) in Expand(
            block.Statements,
            scope
        )) {
            if (stmt is BlockNode { Identifier: "prototype" } row) {
                var rowIdx = protoArr.Count;

                rowScope.CurrentPointer = $"{prototypesPointer}/{rowIdx}";
                rowScope.SourceMap?.Register(
                    jsonPointer: rowScope.CurrentPointer,
                    span: row.Span
                );
                protoArr.AppendNode(item: LowerPrototypeRow(
                    row: row,
                    scope: rowScope
                ));
            } else {
                // `prototypes` lowers to a bare array, so there is no object for a property to land on either.
                ReportUnrecognizedSectionStatement(
                    admitted: "'prototype' rows only",
                    scope: rowScope,
                    section: "prototypes",
                    stmt: stmt
                );
            }
        }

        scope.CurrentPointer = oldPointer;

        return true;
    }
    // `WorldPrototype.Id` is the block's quoted name, the same way `WorldPlacement.Id` is; `document` is an
    // ordinary nested block reached through `LowerBlockToObject` -> `ProcessStatement` -> `LowerBlock`, so a `shape`
    // statement inside it hits `LowerShapeBlock` the same way one at document root does.
    private static JsonObject LowerPrototypeRow(BlockNode row, DocumentScope scope) {
        var rowObj = LowerBlockToObject(
            block: row,
            scope: scope
        );

        if (
            !rowObj.ContainsKey(propertyName: "id") &&
            (DocumentLowering.ResolveBlockName(
            block: row,
            scope: scope
        ) is { Length: > 0 } rowId)
        ) {
            rowObj["id"] = rowId;
        }

        return rowObj;
    }
}
