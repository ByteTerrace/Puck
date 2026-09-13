using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Puck.State;

using Puck.GamingBricks.Forge;

namespace Puck.GamingBricks.Transpiler;

/// <summary>Writes a <c>puck.cartridge.v1</c> document back out as Puck DSL source.</summary>
/// <remarks>One-way, like every Puck decompiler: named constants and templates are a source-only idea and cannot be
/// recovered from the JSON that no longer carries them. What IS guaranteed is the other direction — the source this
/// writes compiles back to the document it was written from, byte for byte.</remarks>
public static class CartridgeDecompiler {
    private const string Indent = "    ";

    // The engine opcodes, and the compound-assignment spelling each is written as. An absent operation is plain `=`.
    private static readonly Dictionary<string, string> Assignments = new(comparer: StringComparer.Ordinal) {
        [nameof(ExpressionOp.Add)] = "+=",
        [nameof(ExpressionOp.BitAnd)] = "&=",
        [nameof(ExpressionOp.BitOr)] = "|=",
        [nameof(ExpressionOp.BitXor)] = "^=",
        [nameof(ExpressionOp.Divide)] = "/=",
        [nameof(ExpressionOp.Modulo)] = "%=",
        [nameof(ExpressionOp.Multiply)] = "*=",
        [nameof(ExpressionOp.ShiftLeft)] = "<<=",
        [nameof(ExpressionOp.ShiftRight)] = ">>=",
        [nameof(ExpressionOp.Subtract)] = "-=",
    };
    // The engine comparisons, and the infix comparator each is written as.
    private static readonly Dictionary<string, string> Comparators = new(comparer: StringComparer.Ordinal) {
        [nameof(ActionStateComparison.Equal)] = "==",
        [nameof(ActionStateComparison.Greater)] = ">",
        [nameof(ActionStateComparison.GreaterOrEqual)] = ">=",
        [nameof(ActionStateComparison.Less)] = "<",
        [nameof(ActionStateComparison.LessOrEqual)] = "<=",
        [nameof(ActionStateComparison.NotEqual)] = "!=",
    };
    // The order sections are written in: the cartridge's own identity first, then its data, then its behaviour.
    // Any key not named here follows in ordinal order, so a section added to the document still round-trips.
    private static readonly string[] SectionOrder = [
        "target", "title", "gameCode", "palettes", "tiles", "map", "variables", "arrays",
        "screens", "sprites", "layers", "raster", "sounds", "save", "scrollX", "scrollY",
    ];

    // A field holding an operand prints as its source spelling; everything else is a plain JSON value.
    private static string ArgumentSource(string key, JsonNode? node) {
        if (node is JsonObject) {
            return CartridgeOperand.ToSource(node: node);
        }

        var sb = new StringBuilder();

        WriteValue(
            indentLevel: 0,
            node: node,
            sb: sb
        );

        return sb.ToString();
    }
    private static string Composed(JsonObject gate, string separator, bool nested) {
        var arms = ((gate["predicates"] as JsonArray) ?? []);
        var inner = string.Join(
            separator: separator,
            values: arms.OfType<JsonObject>().Select(selector: arm => GateSource(
                gate: arm,
                nested: true
            ))
        );

        return (nested
            ? $"({inner})"
            : inner
        );
    }
    // Renders one predicate. A composed arm is parenthesised when it sits inside another, because and binds tighter
    // than or in the language and an unbracketed mixture would read back as a different gate.
    private static string GateSource(JsonObject? gate, bool nested) {
        if (gate is null) {
            // An absent gate always holds, and the DSL has no keyword for one; a tautology is the honest spelling.
            return "0 == 0";
        }

        switch (gate["$type"]?.GetValue<string>()) {
            case "all":
                return Composed(
                    gate: gate,
                    nested: nested,
                    separator: " and "
                );
            case "any":
                return Composed(
                    gate: gate,
                    nested: nested,
                    separator: " or "
                );
            case "not":
                return $"not {GateSource(
                    gate: (gate["predicate"] as JsonObject),
                    nested: true
                )}";
            default: {
                    var left = CartridgeOperand.ToSource(node: gate["left"]);
                    var right = CartridgeOperand.ToSource(node: gate["right"]);
                    var comparison = (gate["comparison"]?.GetValue<string>() ?? nameof(ActionStateComparison.Equal));
                    var comparator = (Comparators.TryGetValue(
                        key: comparison,
                        value: out var found
                    )
                        ? found
                        : "=="
                    );

                    // A button read against one is what a key test lowered to, and the key spelling is the readable half.
                    if (
                        (comparison == nameof(ActionStateComparison.Equal)) &&
                        (right == "1") &&
                        CartridgeExpressions.TryKey(
                        button: out var button,
                        mode: out var mode,
                        name: left
                    )
                    ) {
                        return $"key({button}, {mode})";
                    }

                    return $"{left} {comparator} {right}";
                }
        }
    }
    private static IEnumerable<string> OrderedSections(JsonObject document) {
        var remaining = document
            .Select(selector: static pair => pair.Key)
            .Where(predicate: static key => (key is not ("schema" or "rules")))
            .ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var key in SectionOrder) {
            if (remaining.Remove(item: key)) {
                yield return key;
            }
        }

        foreach (var key in remaining.OrderBy(
            keySelector: static key => key,
            comparer: StringComparer.Ordinal
        )) {
            yield return key;
        }
    }
    private static void WriteBody(StringBuilder sb, JsonArray? body, int indentLevel) {
        if (body is null) {
            return;
        }

        foreach (var step in body.OfType<JsonObject>()) {
            WriteStep(
                indentLevel: indentLevel,
                sb: sb,
                step: step
            );
        }
    }
    private static void WriteCallStep(StringBuilder sb, JsonObject step, string kind, string pad) {
        sb.Append(value: pad).Append(value: kind).Append(value: '(');

        var first = true;

        foreach (var pair in step.OrderBy(
            keySelector: static pair => pair.Key,
            comparer: StringComparer.Ordinal
        )) {
            if (pair.Key == "kind") {
                continue;
            }

            if (!first) {
                sb.Append(value: ", ");
            }

            first = false;
            sb.Append(value: pair.Key).Append(value: ": ").Append(value: ArgumentSource(
                key: pair.Key,
                node: pair.Value
            ));
        }

        sb.Append(value: ")\n");
    }
    // One field, in the one spelling its value's shape calls for: a container is a block, a scalar takes a colon.
    // The colon is what tells a reader "this is a leaf", so it never appears in front of a '{' or a '['.
    private static void WriteField(StringBuilder sb, string key, JsonNode? node, int indentLevel) {
        sb.Append(value: key).Append(value: ((node is (JsonObject or JsonArray))
            ? " "
            : ": "));
        WriteValue(
            indentLevel: indentLevel,
            node: node,
            sb: sb
        );
        sb.Append(value: '\n');
    }
    private static void WriteRule(StringBuilder sb, JsonObject rule) {
        sb.Append(value: "rule \"").Append(value: (rule["name"]?.GetValue<string>() ?? string.Empty)).Append(value: "\" {\n");

        if (rule["when"] is JsonObject gate) {
            sb.Append(value: Indent).Append(value: "when ").Append(value: GateSource(
                gate: gate,
                nested: false
            )).Append(value: '\n');
        }

        WriteBody(
            sb: sb,
            body: (rule["body"] as JsonArray),
            indentLevel: 1
        );
        sb.Append(value: "}\n");
    }
    private static void WriteStep(StringBuilder sb, JsonObject step, int indentLevel) {
        var pad = string.Concat(values: Enumerable.Repeat(
            count: indentLevel,
            element: Indent
        ));

        switch (step["kind"]?.GetValue<string>()) {
            case "set": {
                    var operation = (step["operation"]?.GetValue<string>() ?? string.Empty);
                    var spelling = (Assignments.TryGetValue(
                        key: operation,
                        value: out var found
                    )
                        ? found
                        : "="
                    );

                    sb.Append(value: pad)
                        .Append(value: CartridgeOperand.TargetToSource(node: step["target"]))
                        .Append(value: ' ').Append(value: spelling).Append(value: ' ')
                        .Append(value: CartridgeOperand.ToSource(node: step["value"]))
                        .Append(value: '\n');

                    break;
                }

            case "if": {
                    sb.Append(value: pad).Append(value: "if ").Append(value: GateSource(
                        gate: (step["when"] as JsonObject),
                        nested: false
                    )).Append(value: " {\n");
                    WriteBody(
                        sb: sb,
                        body: (step["then"] as JsonArray),
                        indentLevel: (indentLevel + 1)
                    );

                    if (step["else"] is JsonArray otherwise) {
                        sb.Append(value: pad).Append(value: "} else {\n");
                        WriteBody(
                            body: otherwise,
                            indentLevel: (indentLevel + 1),
                            sb: sb
                        );
                    }

                    sb.Append(value: pad).Append(value: "}\n");

                    break;
                }

            case "repeat": {
                    sb.Append(value: pad)
                        .Append(value: "repeat ").Append(value: (step["count"]?.GetValue<int>() ?? 1).ToString(provider: CultureInfo.InvariantCulture))
                        .Append(value: " as ").Append(value: (step["index"]?.GetValue<string>() ?? "i"))
                        .Append(value: " {\n");
                    WriteBody(
                        sb: sb,
                        body: (step["body"] as JsonArray),
                        indentLevel: (indentLevel + 1)
                    );
                    sb.Append(value: pad).Append(value: "}\n");

                    break;
                }

            case "break":
                sb.Append(value: pad).Append(value: "break\n");

                break;

            case { } kind:
                WriteCallStep(
                    kind: kind,
                    pad: pad,
                    sb: sb,
                    step: step
                );

                break;
        }
    }
    // Data sections print as the DSL value that lowers back to them. A long numeric array wraps at a fixed column
    // count so the source stays readable without changing what it means.
    private static void WriteValue(StringBuilder sb, JsonNode? node, int indentLevel) {
        switch (node) {
            case null:
                sb.Append(value: "null");

                break;

            case JsonArray arr: {
                    if (arr.Count == 0) {
                        sb.Append(value: "[]");

                        break;
                    }

                    var pad = string.Concat(values: Enumerable.Repeat(
                        count: (indentLevel + 1),
                        element: Indent
                    ));

                    sb.Append(value: "[\n");

                    foreach (var item in arr) {
                        sb.Append(value: pad);
                        WriteValue(
                            indentLevel: (indentLevel + 1),
                            node: item,
                            sb: sb
                        );
                        sb.Append(value: '\n');
                    }

                    sb.Append(value: string.Concat(values: Enumerable.Repeat(
                        count: indentLevel,
                        element: Indent
                    ))).Append(value: ']');

                    break;
                }

            case JsonObject obj: {
                    if (obj.Count == 0) {
                        sb.Append(value: "{}");

                        break;
                    }

                    var pad = string.Concat(values: Enumerable.Repeat(
                        count: (indentLevel + 1),
                        element: Indent
                    ));

                    sb.Append(value: "{\n");

                    foreach (var pair in obj) {
                        sb.Append(value: pad);
                        WriteField(
                            sb: sb,
                            key: pair.Key,
                            node: pair.Value,
                            indentLevel: (indentLevel + 1)
                        );
                    }

                    sb.Append(value: string.Concat(values: Enumerable.Repeat(
                        count: indentLevel,
                        element: Indent
                    ))).Append(value: '}');

                    break;
                }

            case JsonValue value: {
                    if (value.TryGetValue<string>(value: out var text)) {
                        sb.Append(value: '"').Append(value: text.Replace(
                            newValue: "\\\\",
                            oldValue: "\\"
                        ).Replace(
                            newValue: "\\\"",
                            oldValue: "\""
                        )).Append(value: '"');
                    } else if (value.TryGetValue<bool>(value: out var flag)) {
                        sb.Append(value: (flag
                            ? "true"
                            : "false"));
                    } else if (value.TryGetValue<long>(value: out var number)) {
                        sb.Append(value: number.ToString(provider: CultureInfo.InvariantCulture));
                    } else if (value.TryGetValue<double>(value: out var real)) {
                        sb.Append(value: real.ToString(
                            format: "R",
                            provider: CultureInfo.InvariantCulture
                        ));
                    } else {
                        sb.Append(value: value.ToJsonString());
                    }

                    break;
                }
        }
    }

    /// <summary>Writes a cartridge document out as Puck DSL source.</summary>
    /// <param name="document">The cartridge JSON.</param>
    /// <returns>The source text, newline-terminated.</returns>
    public static string Decompile(JsonObject document) {
        ArgumentNullException.ThrowIfNull(document);

        var sb = new StringBuilder();

        sb.Append(value: "schema: \"").Append(value: (document["schema"]?.GetValue<string>() ?? CartridgeVocabulary.Schema)).Append(value: "\"\n");

        foreach (var key in OrderedSections(document: document)) {
            sb.Append(value: '\n');
            WriteField(
                sb: sb,
                key: key,
                node: document[key],
                indentLevel: 0
            );
        }

        if (document["rules"] is JsonArray rules) {
            if (rules.Count == 0) { sb.Append(value: "\nrules []\n"); }
            foreach (var rule in rules.OfType<JsonObject>()) {
                sb.Append(value: '\n');
                WriteRule(
                    rule: rule,
                    sb: sb
                );
            }
        }

        return sb.ToString();
    }
}
