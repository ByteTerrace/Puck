using System.Text.Json.Nodes;
using Puck.SignedDistance;
using Puck.World.Transpiler.Ast;
using Puck.World.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Lowering;

// `shape Type "name" { }` (§4.1, a creation-document row — CreationDocument.Shapes, the live successor to the dead
// `solid`/`"solids"` collector this renames in place) and `placements { policy: { } placement "id" { } }` (§4.2).
public static partial class WorldDocumentEmitter {
    private static void LowerShapeBlock(BlockNode block, JsonObject parent, EvaluationScope scope) {
        if (parent["shapes"] is not JsonArray shapesArr) {
            shapesArr = [];
            parent["shapes"] = shapesArr;
        }

        // The shared block grammar puts a lone identifier before '{' into Name (no target); with a second one, the
        // first is Target and the second Name. `shape` always carries a type, optionally a name, so the type is
        // whichever slot the lone-identifier case fills and the name is Name only when Target is also present.
        var type = block.Target ?? block.Name;
        var shapeName = (block.Target is not null) ? block.Name : null;

        var shapeIdx = shapesArr.Count;
        var shapePointer = $"{scope.CurrentPointer}/shapes/{shapeIdx}";
        scope.SourceMap?.Register(shapePointer, block.Span);

        var oldPointer = scope.CurrentPointer;
        scope.CurrentPointer = shapePointer;
        var shapeObj = LowerBlockToObject(block, scope);
        scope.CurrentPointer = oldPointer;

        if (type is not null) {
            if (!Enum.TryParse<SdfSolidPrimitive>(type, ignoreCase: false, out _)) {
                scope.Diagnostics.ReportError(PuckDiagnosticCodes.UnknownShapeType, $"'{type}' is not a recognized shape type", block.Span);
            }
            shapeObj["type"] = type;
        }
        if (shapeName is not null) {
            shapeObj["name"] = shapeName;
        }

        foreach (var key in WorldDocumentRowDefaults.ShapeKeys) {
            if (!shapeObj.ContainsKey(key)) {
                shapeObj[key] = WorldDocumentRowDefaults.ShapeDefault(key, shapeIdx);
            }
        }

        shapesArr.AppendNode(shapeObj);
    }

    private static void LowerPlacementsBlock(BlockNode block, JsonObject parent, EvaluationScope scope) {
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
        scope.SourceMap?.Register(placementsPointer, block.Span);

        foreach (var stmt in block.Statements) {
            if (stmt is BlockNode { Identifier: "placement" } row) {
                var rowIdx = rowsArr.Count;
                scope.CurrentPointer = $"{placementsPointer}/rows/{rowIdx}";
                scope.SourceMap?.Register(scope.CurrentPointer, row.Span);
                rowsArr.AppendNode(LowerPlacementRow(row, scope));
            } else if (stmt is PropertyNode prop) {
                scope.CurrentPointer = placementsPointer;
                placementsObj[prop.Name] = LowerExpression(prop.Value, scope, prop.Name);
            }
        }

        scope.CurrentPointer = oldPointer;
    }

    private static JsonObject LowerPlacementRow(BlockNode row, EvaluationScope scope) {
        var rowObj = LowerBlockToObject(row, scope);

        // Deviates from the addon/shape precedent of mapping a block's quoted name to "name": WorldPlacement's
        // identity field is literally Id.
        if (!rowObj.ContainsKey("id") && !string.IsNullOrEmpty(row.Name)) {
            rowObj["id"] = row.Name;
        }

        if (rowObj["prototype"] is { } prototypeNode) {
            rowObj.Remove("prototype");
            if (!rowObj.ContainsKey("prototypeId")) {
                rowObj["prototypeId"] = prototypeNode.DeepClone();
            }
        }

        // The bare `solid` flag (PropertyNode never carries it — FlagStatementNode has no ProcessStatement case, so
        // it never reached rowObj through LowerBlockToObject) stands in for `solid: { margin: 0 }`. PUCK028 already
        // refused authoring both spellings on the same row; the explicit object wins when it did.
        var sawBareSolid = row.Statements.Any(static s => s is FlagStatementNode { Name: "solid" });
        if (sawBareSolid && !rowObj.ContainsKey("solid")) {
            rowObj["solid"] = WorldDocumentRowDefaults.BareSolid();
        }

        // Only a row carrying its own prototypeId is a whole placement; one without it is either a basis-merge
        // directive or a partial row a basis chain completes, and filling a default there would override the value
        // composition was about to supply. The decompiler elides these two fields on exactly the same condition.
        if (rowObj.ContainsKey("prototypeId")) {
            foreach (var key in WorldDocumentRowDefaults.PlacementKeys) {
                if (!rowObj.ContainsKey(key)) {
                    rowObj[key] = WorldDocumentRowDefaults.PlacementDefault(key);
                }
            }
        }

        return rowObj;
    }
}
