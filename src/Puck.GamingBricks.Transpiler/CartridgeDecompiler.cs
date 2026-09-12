using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck.GamingBricks.Transpiler;

/// <summary>Writes a <c>puck.cartridge.v1</c> document back out as Puck DSL source.</summary>
/// <remarks>One-way, like every Puck decompiler: named constants and templates are a source-only idea and cannot be
/// recovered from the JSON that no longer carries them. What IS guaranteed is the other direction — the source this
/// writes compiles back to the document it was written from, byte for byte.</remarks>
public static class CartridgeDecompiler {
    private const string Indent = "    ";

    // The `set` operations, and the compound-assignment spelling each is written as. "set" is plain `=`.
    private static readonly Dictionary<string, string> s_assignments = new(StringComparer.Ordinal) {
        ["add"] = "+=",
        ["and"] = "&=",
        ["div"] = "/=",
        ["mod"] = "%=",
        ["mul"] = "*=",
        ["or"] = "|=",
        ["set"] = "=",
        ["shl"] = "<<=",
        ["shr"] = ">>=",
        ["subtract"] = "-=",
        ["xor"] = "^=",
    };

    // The cartridge comparisons, and the infix comparator each is written as.
    private static readonly Dictionary<string, string> s_comparators = new(StringComparer.Ordinal) {
        ["eq"] = "==",
        ["ge"] = ">=",
        ["gt"] = ">",
        ["le"] = "<=",
        ["lt"] = "<",
        ["ne"] = "!=",
    };

    // The order sections are written in: the cartridge's own identity first, then its data, then its behaviour.
    // Any key not named here follows in ordinal order, so a section added to the document still round-trips.
    private static readonly string[] s_sectionOrder = [
        "target", "title", "gameCode", "palettes", "tiles", "map", "variables", "arrays",
        "screens", "sprites", "layers", "raster", "sounds", "save", "scrollX", "scrollY",
    ];

    /// <summary>Writes a cartridge document out as Puck DSL source.</summary>
    /// <param name="document">The cartridge JSON.</param>
    /// <returns>The source text, newline-terminated.</returns>
    public static string Decompile(JsonObject document) {
        ArgumentNullException.ThrowIfNull(document);

        var sb = new StringBuilder();

        sb.Append("schema: \"").Append(document["schema"]?.GetValue<string>() ?? CartridgeVocabulary.Schema).Append("\"\n");

        foreach (var key in OrderedSections(document: document)) {
            sb.Append('\n');
            WriteField(sb: sb, key: key, node: document[key], indentLevel: 0);
        }

        if (document["rules"] is JsonArray rules) {
            foreach (var rule in rules.OfType<JsonObject>()) {
                sb.Append('\n');
                WriteRule(sb: sb, rule: rule);
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> OrderedSections(JsonObject document) {
        var remaining = document
            .Select(static pair => pair.Key)
            .Where(static key => (key is not ("schema" or "rules")))
            .ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var key in s_sectionOrder) {
            if (remaining.Remove(item: key)) {
                yield return key;
            }
        }

        foreach (var key in remaining.OrderBy(keySelector: static key => key, comparer: StringComparer.Ordinal)) {
            yield return key;
        }
    }

    private static void WriteRule(StringBuilder sb, JsonObject rule) {
        sb.Append("rule \"").Append(rule["name"]?.GetValue<string>() ?? string.Empty).Append("\" {\n");

        if ((rule["when"] is JsonArray conditions) && (conditions.Count > 0)) {
            sb.Append(Indent).Append("when ").Append(ConditionsSource(conditions: conditions)).Append('\n');
        }

        WriteBody(sb: sb, body: (rule["body"] as JsonArray), indentLevel: 1);
        sb.Append("}\n");
    }

    private static void WriteBody(StringBuilder sb, JsonArray? body, int indentLevel) {
        if (body is null) {
            return;
        }

        foreach (var step in body.OfType<JsonObject>()) {
            WriteStep(sb: sb, step: step, indentLevel: indentLevel);
        }
    }

    private static void WriteStep(StringBuilder sb, JsonObject step, int indentLevel) {
        var pad = string.Concat(Enumerable.Repeat(element: Indent, count: indentLevel));

        switch (step["kind"]?.GetValue<string>()) {
            case "set": {
                var operation = (step["operation"]?.GetValue<string>() ?? "set");
                var spelling = (s_assignments.TryGetValue(key: operation, value: out var found) ? found : "=");

                sb.Append(pad)
                    .Append(CartridgeOperand.ToSource(node: step["target"]))
                    .Append(' ').Append(spelling).Append(' ')
                    .Append(CartridgeOperand.ToSource(node: step["value"]))
                    .Append('\n');

                break;
            }

            case "if": {
                sb.Append(pad).Append("if ").Append(ConditionsSource(conditions: (step["when"] as JsonArray))).Append(" {\n");
                WriteBody(sb: sb, body: (step["then"] as JsonArray), indentLevel: (indentLevel + 1));

                if (step["else"] is JsonArray otherwise) {
                    sb.Append(pad).Append("} else {\n");
                    WriteBody(sb: sb, body: otherwise, indentLevel: (indentLevel + 1));
                }

                sb.Append(pad).Append("}\n");

                break;
            }

            case "repeat": {
                sb.Append(pad)
                    .Append("repeat ").Append((step["count"]?.GetValue<int>() ?? 1).ToString(provider: CultureInfo.InvariantCulture))
                    .Append(" as ").Append(step["index"]?.GetValue<string>() ?? "i")
                    .Append(" {\n");
                WriteBody(sb: sb, body: (step["body"] as JsonArray), indentLevel: (indentLevel + 1));
                sb.Append(pad).Append("}\n");

                break;
            }

            case "break":
                sb.Append(pad).Append("break\n");

                break;

            case { } kind:
                WriteCallStep(sb: sb, step: step, kind: kind, pad: pad);

                break;
        }
    }

    private static void WriteCallStep(StringBuilder sb, JsonObject step, string kind, string pad) {
        sb.Append(pad).Append(kind).Append('(');

        var first = true;

        foreach (var pair in step.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
            if (pair.Key == "kind") {
                continue;
            }

            if (!first) {
                sb.Append(", ");
            }

            first = false;
            sb.Append(pair.Key).Append(": ").Append(ArgumentSource(key: pair.Key, node: pair.Value));
        }

        sb.Append(")\n");
    }

    // A field holding an operand prints as its source spelling; everything else is a plain JSON value.
    private static string ArgumentSource(string key, JsonNode? node) {
        if (node is JsonObject) {
            return CartridgeOperand.ToSource(node: node);
        }

        var sb = new StringBuilder();

        WriteValue(sb: sb, node: node, indentLevel: 0);

        return sb.ToString();
    }

    private static string ConditionsSource(JsonArray? conditions) {
        if ((conditions is null) || (conditions.Count == 0)) {
            // An empty gate always holds, and the DSL has no keyword for one; a tautology is the honest spelling.
            return "0 == 0";
        }

        return string.Join(separator: " and ", values: conditions.OfType<JsonObject>().Select(selector: ConditionSource));
    }

    private static string ConditionSource(JsonObject condition) {
        if (condition["kind"]?.GetValue<string>() == "key") {
            return $"key({condition["key"]?.GetValue<string>()}, {condition["mode"]?.GetValue<string>()})";
        }

        var comparison = (condition["comparison"]?.GetValue<string>() ?? "eq");
        var comparator = (s_comparators.TryGetValue(key: comparison, value: out var found) ? found : "==");

        return $"{CartridgeOperand.ToSource(node: condition["left"])} {comparator} {CartridgeOperand.ToSource(node: condition["right"])}";
    }

    // One field, in the one spelling its value's shape calls for: a container is a block, a scalar takes a colon.
    // The colon is what tells a reader "this is a leaf", so it never appears in front of a '{' or a '['.
    private static void WriteField(StringBuilder sb, string key, JsonNode? node, int indentLevel) {
        sb.Append(key).Append((node is (JsonObject or JsonArray)) ? " " : ": ");
        WriteValue(sb: sb, node: node, indentLevel: indentLevel);
        sb.Append('\n');
    }

    // Data sections print as the DSL value that lowers back to them. A long numeric array wraps at a fixed column
    // count so the source stays readable without changing what it means.
    private static void WriteValue(StringBuilder sb, JsonNode? node, int indentLevel) {
        switch (node) {
            case null:
                sb.Append("null");

                break;

            case JsonArray arr: {
                if (arr.Count == 0) {
                    sb.Append("[]");

                    break;
                }

                var pad = string.Concat(Enumerable.Repeat(element: Indent, count: (indentLevel + 1)));

                sb.Append("[\n");

                foreach (var item in arr) {
                    sb.Append(pad);
                    WriteValue(sb: sb, node: item, indentLevel: (indentLevel + 1));
                    sb.Append('\n');
                }

                sb.Append(string.Concat(Enumerable.Repeat(element: Indent, count: indentLevel))).Append(']');

                break;
            }

            case JsonObject obj: {
                if (obj.Count == 0) {
                    sb.Append("{}");

                    break;
                }

                var pad = string.Concat(Enumerable.Repeat(element: Indent, count: (indentLevel + 1)));

                sb.Append("{\n");

                foreach (var pair in obj) {
                    sb.Append(pad);
                    WriteField(sb: sb, key: pair.Key, node: pair.Value, indentLevel: (indentLevel + 1));
                }

                sb.Append(string.Concat(Enumerable.Repeat(element: Indent, count: indentLevel))).Append('}');

                break;
            }

            case JsonValue value: {
                if (value.TryGetValue<string>(value: out var text)) {
                    sb.Append('"').Append(text.Replace(oldValue: "\\", newValue: "\\\\").Replace(oldValue: "\"", newValue: "\\\"")).Append('"');
                } else if (value.TryGetValue<bool>(value: out var flag)) {
                    sb.Append(flag ? "true" : "false");
                } else if (value.TryGetValue<long>(value: out var number)) {
                    sb.Append(number.ToString(provider: CultureInfo.InvariantCulture));
                } else if (value.TryGetValue<double>(value: out var real)) {
                    sb.Append(real.ToString(format: "R", provider: CultureInfo.InvariantCulture));
                } else {
                    sb.Append(value.ToJsonString());
                }

                break;
            }
        }
    }
}
