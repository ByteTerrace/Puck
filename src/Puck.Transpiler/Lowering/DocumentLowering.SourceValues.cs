using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Lowering;

public static partial class DocumentLowering {
    private const string ValuePointerKey = "DocumentValuePointer";

    private sealed record ValueLocation(string Pointer, int IndexOffset);

    private static string? ValueChild(DocumentScope scope, string member) =>
        ((scope.Annotations.GetValueOrDefault(key: ValuePointerKey) is ValueLocation location)
            ? ((location.Pointer + "/") + member.Replace(comparisonType: StringComparison.Ordinal, newValue: "~0", oldValue: "~")
                .Replace(comparisonType: StringComparison.Ordinal, newValue: "~1", oldValue: "/"))
            : null);
    private static T AtValuePointer<T>(DocumentScope scope, string? pointer, Func<T> lower, int indexOffset = 0) {
        if (pointer is null) { return lower(); }
        var previous = scope.Annotations.GetValueOrDefault(key: ValuePointerKey);

        scope.Annotations[ValuePointerKey] = new ValueLocation(IndexOffset: indexOffset, Pointer: pointer);
        try { return lower(); } finally { scope.Annotations[ValuePointerKey] = previous; }
    }
    private static JsonNode? LowerElement(ExpressionNode element, DocumentScope scope, string? fieldKey, int index) {
        if (scope.Annotations.GetValueOrDefault(key: ValuePointerKey) is ValueLocation location) { index += location.IndexOffset; }
        var pointer = ValueChild(scope: scope, member: index.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));

        if (pointer is not null) { scope.SourceMap?.RegisterValue(jsonPointer: pointer, span: element.Span, form: DocumentValueForm.Unclassified); }
        return AtValuePointer(scope: scope, pointer: pointer, lower: () => LowerValue(expr: element, fieldKey: fieldKey, scope: scope));
    }
}
