using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    private static readonly JsonTypeInfo<CellSetExpression> CellSetTypeInfo =
        ((JsonTypeInfo<CellSetExpression>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(CellSetExpression)));

    /// <summary>Lowers a <c>set</c> declaration into one row of the document's <c>sets</c> member.</summary>
    /// <remarks>The expression is parsed and re-serialized through <see cref="CellSetSpelling"/> and
    /// <see cref="WorldJsonContext"/>, so a spelling the algebra refuses is refused here, at the declaring line,
    /// rather than at boot, and the discriminators the document carries cannot drift from the model's.</remarks>
    private static void LowerCellSetDeclaration(
        JsonObject parent,
        CellSetDeclarationNode setNode,
        DocumentScope scope
    ) {
        if (!CellSetSpelling.TryParse(
            error: out var error,
            expression: out var expression,
            text: setNode.Expression
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.CellSetExpressionInvalid,
                message: $"set '{setNode.Name}': {error}",
                span: setNode.Span
            );

            return;
        }

        if (parent["sets"] is not JsonArray rows) {
            rows = [];
            parent["sets"] = rows;
        }

        scope.SourceMap?.Register(
            jsonPointer: $"{scope.CurrentPointer}/sets/{rows.Count}",
            span: setNode.Span
        );
        rows.AppendNode(item: new JsonObject {
            ["name"] = JsonValue.Create(value: setNode.Name),
            ["set"] = JsonSerializer.SerializeToNode(
                jsonTypeInfo: CellSetTypeInfo,
                value: expression!
            ),
        });
    }
}
