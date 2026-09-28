using System.Text.Json.Nodes;
using System.Text;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Decompiler;

public static partial class WorldDecompiler {
    private static bool IsKeyedValue(JsonObject value) => ((value.Count == 2) &&
        (value["clock"] is JsonValue clock) && clock.TryGetValue<string>(value: out _) && (value["keys"] is JsonArray));

    private static string FormatKeys(JsonObject value, int indentLevel, Type? context) {
        var indent = new string(c: ' ', count: (indentLevel * 4));
        var rowIndent = (indent + "    ");
        var memberIndent = (rowIndent + "    ");
        var result = new StringBuilder();

        result.Append(value: "keys(clock: ").Append(value: DocumentLowering.BareSpelling(form: DocumentValueForm.Name, written: value["clock"]!.GetValue<string>())).AppendLine(value: ") [");
        foreach (var item in value["keys"]!.AsArray()) {
            if (item is not JsonObject row) {
                result.Append(value: rowIndent).AppendLine(value: FormatValue(node: item, indentLevel: (indentLevel + 1), context: context));
                continue;
            }
            result.Append(value: rowIndent).AppendLine(value: "{");
            foreach (var (name, field) in row) {
                var form = ((name == "ease") ? WorldArgumentForm.Choice : WorldArgumentForm.Unclassified);

                result.Append(value: memberIndent).Append(value: PuckPrinter.PrintPropertyName(level: (indentLevel + 2), name: name))
                    .Append(value: FieldSeparator(value: field)).AppendLine(value: FormatArgument(node: field, form: form, indentLevel: (indentLevel + 2), context: context));
            }
            result.Append(value: rowIndent).AppendLine(value: "}");
        }
        return result.Append(value: indent).Append(value: ']').ToString();
    }
}
