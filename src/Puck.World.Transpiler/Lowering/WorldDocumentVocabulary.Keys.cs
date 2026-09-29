using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

public sealed partial class WorldDocumentVocabulary {
    private sealed record KeyCallContext(object? Value, string? Field);
    private sealed record KeyRowContext(KeyCallContext Call);
    private sealed record KeyValueContext(KeyCallContext Call);
    private static readonly object KeyTimeContext = new();

    private bool TryLowerKeys(ExpressionNode expression, DocumentScope scope, string? fieldKey, out JsonNode? value) {
        var position = DocumentLowering.MemberContext(scope: scope);

        if (ReferenceEquals(objA: position, objB: KeyTimeContext)) {
            value = DocumentLowering.At(context: null, scope: scope,
                lower: () => DocumentLowering.LowerValue(expr: expression, fieldKey: "atSeconds", scope: scope));
            return true;
        }
        if (position is KeyValueContext key) {
            if (expression is CallExpressionNode { Name: "keys" }) {
                scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.InvalidValue,
                    message: $"'{key.Call.Field ?? "value"}' key values cannot contain keys; compose clocks through timeline.clocks.phase", span: expression.Span);
                value = null;
            } else {
                value = DocumentLowering.At(context: key.Call.Value, scope: scope,
                    lower: () => DocumentLowering.LowerValue(expr: expression, fieldKey: key.Call.Field, scope: scope));
            }
            return true;
        }
        if ((expression is not CallExpressionNode { Name: "keys" } call) || !call.Arguments.Any(predicate: static argument => argument.TrailingBody) || (NameCallBody(callName: call.Name) is not { } bodyName)) {
            value = null;
            return false;
        }
        var holder = new KeyCallContext(Value: position, Field: fieldKey);
        var lowered = new JsonObject();

        foreach (var argument in call.Arguments) {
            if ((argument.Name is not { } name) || ((name != "clock") && (name != bodyName)) || lowered.ContainsKey(propertyName: name)) {
                scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.InvalidValue,
                    message: $"'keys' admits one named 'clock' argument and its trailing '{bodyName}' array; '{argument.Name ?? "positional argument"}' is not admitted", span: argument.Span);
                continue;
            }
            lowered[name] = DocumentLowering.LowerMember(holder: holder, holderName: call.Name, memberName: name,
                value: argument.Value, fieldKey: name, scope: scope);
        }
        if (!lowered.ContainsKey(propertyName: "clock")) {
            scope.Diagnostics.ReportError(code: PuckDiagnosticCodes.InvalidValue, message: "'keys' requires the named 'clock' argument", span: call.Span);
        }
        value = lowered;
        return true;
    }

    private static object? KeyMemberContext(object context, string memberName) => context switch {
        KeyCallContext call when (memberName == "keys") => new KeyRowContext(Call: call),
        KeyRowContext row when (memberName == "value") => new KeyValueContext(Call: row.Call),
        KeyRowContext when (memberName == "at") => KeyTimeContext,
        _ => null,
    };
}
