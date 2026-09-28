using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

// A graph row's bound parameters, written one `parameter pass.member = value` statement a member inside its `graph`
// block and lowered to `parameters.<pass>.<member>`.
public static partial class WorldDocumentEmitter {
    private const string GraphParametersKey = "parameters";

    private static bool InGraphRow(DocumentScope scope) => ((DocumentLowering.MemberContext(scope: scope) is Type row) && (row == typeof(WorldViewGraph)));
    private static void LowerGraphParameter(GraphParameterNode parameter, JsonObject target, DocumentScope scope) {
        if (!InGraphRow(scope: scope)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.GraphParameter,
                message: $"'parameter {parameter.Pass}.{parameter.Member}' binds a graph instance's parameter, so it stands inside a `graph` block",
                span: parameter.Span
            );
            return;
        }

        var pointer = $"{scope.CurrentPointer}/{GraphParametersKey}";

        // The member's first statement stands for it in the source map.
        if (target[GraphParametersKey] is not JsonObject parameters) {
            parameters = [];
            target[GraphParametersKey] = parameters;
            scope.SourceMap?.Register(jsonPointer: pointer, span: parameter.Span);
        }
        if (parameters[parameter.Pass] is not JsonObject pass) {
            pass = [];
            parameters[parameter.Pass] = pass;
        }
        if (pass.ContainsKey(propertyName: parameter.Member)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.GraphParameter,
                message: $"'{parameter.Pass}.{parameter.Member}' is bound twice in this graph block; keep one `parameter` statement for it",
                span: parameter.Span
            );
            return;
        }

        scope.SourceMap?.Register(jsonPointer: $"{pointer}/{parameter.Pass}/{parameter.Member}", span: parameter.Span);
        pass[parameter.Member] = DocumentLowering.At(
            context: null,
            lower: () => DocumentLowering.LowerValue(expr: parameter.Value, fieldKey: parameter.Member, scope: scope),
            scope: scope
        );
    }
    // A graph row's `parameters` has one spelling, the `parameter` statement, so the raw member is refused where it is
    // written.
    private static bool RefusesRawGraphParameters(string name, SourceSpan span, DocumentScope scope) {
        if (!string.Equals(a: name, b: GraphParametersKey, comparisonType: StringComparison.Ordinal) || !InGraphRow(scope: scope)) {
            return false;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.GraphParameter,
            message: "a graph instance's parameters are written one statement a member, `parameter <pass>.<member> = <value>`, not as a `parameters` block",
            span: span
        );
        return true;
    }
}
