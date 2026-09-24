using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Puck.State;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler.Decompiler;

// The `patterns` section prints as one `pattern` declaration per row. A row is sugarable only when its whole
// content has a spelling: the members the declaration can carry, an alphabet of bare symbol names, and a language
// whose n-ary nodes carry two or more items (one item prints as itself and would re-lower as itself).
public static partial class WorldDecompiler {
    // The keys a `pattern` declaration can carry are its construct's own description, `pattern` itself among them
    // because the emitter compiles the machine into the row while it lowers.
    private static readonly HashSet<string> PatternNodeKeys = WorldConstructs.NodeKeysOf(enclosing: null, keyword: "pattern");
    private static readonly JsonTypeInfo<PatternNode> PatternNodeTypeInfo =
        ((JsonTypeInfo<PatternNode>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(PatternNode)));

    private static bool TryReadPatternNode(JsonNode? node, out PatternNode? pattern) {
        pattern = null;

        if (node is not JsonObject) {
            return false;
        }

        try {
            pattern = System.Text.Json.JsonSerializer.Deserialize(
                jsonTypeInfo: PatternNodeTypeInfo,
                json: node.ToJsonString()
            );
        } catch (System.Text.Json.JsonException) {
            return false;
        }

        return (pattern is not null);
    }
    private static bool CanSugarPatterns(JsonArray patterns) {
        if (patterns.Count == 0) {
            return false;
        }

        foreach (var item in patterns) {
            // The declaration prints its name and kind bare, so the name is one the parser reads as an identifier and
            // the kind is spelled exactly as its enum member: the engine's converter accepts any casing, the grammar
            // does not.
            if (
                (item is not JsonObject row) ||
                (row["name"] is not JsonValue rowName) ||
                !IdentifierSpelling.IsName(text: rowName.ToString()) ||
                (row["kind"] is not JsonValue rowKind) ||
                !Enum.GetNames<CellKind>().Contains(value: rowKind.ToString(), comparer: StringComparer.Ordinal) ||
                (row["symbols"] is not JsonArray symbols) ||
                (symbols.Count == 0)
            ) {
                return false;
            }

            foreach (var (key, _) in row) {
                if (!PatternNodeKeys.Contains(item: key)) {
                    return false;
                }
            }
            foreach (var symbol in symbols) {
                if (
                    (symbol is not JsonObject entry) ||
                    (entry["name"] is not JsonValue name) ||
                    (entry["min"] is not JsonValue) ||
                    (entry["max"] is not JsonValue) ||
                    (entry.Count != 3) ||
                    !IdentifierSpelling.IsIdentifier(text: name.ToString())
                ) {
                    return false;
                }
            }
            if (
                !TryReadPatternNode(
                node: row["pattern"],
                pattern: out var pattern
            ) ||
                !PatternSpelling.TryPrint(
                node: pattern,
                text: out _
            )
            ) {
                return false;
            }
        }

        return true;
    }
    private static string PatternBound(JsonNode? node) {
        var text = (node?.ToString() ?? "0");

        return (decimal.TryParse(
            provider: CultureInfo.InvariantCulture,
            result: out var value,
            s: text
        )
            ? value.ToString(provider: CultureInfo.InvariantCulture)
            : text
        );
    }
    private static void DecompilePatternsBlock(StringBuilder sb, JsonArray patterns) {
        var first = true;

        foreach (var item in patterns) {
            var row = ((JsonObject)item!);

            if (!first) {
                sb.AppendLine();
            }
            first = false;

            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"pattern {row["name"]} : {row["kind"]} {{"
            );

            if (row["attribute"] is JsonValue attribute) {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  attribute: \"{attribute}\""
                );
            }
            if (row["value"] is { } value) {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  value: \"{WorldExpressionJson.Text(node: value)}\""
                );
            }
            if (row["maxStates"] is JsonValue budget) {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  maxStates: {budget}"
                );
            }

            sb.AppendLine(value: "  symbols {");

            foreach (var symbol in ((JsonArray)row["symbols"]!)) {
                var entry = ((JsonObject)symbol!);
                var minimum = PatternBound(node: entry["min"]);
                var maximum = PatternBound(node: entry["max"]);

                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"    {entry["name"]} = {((minimum == maximum)
                        ? minimum
                        : $"{minimum}..{maximum}"
                    )}"
                );
            }
            sb.AppendLine(value: "  }");

            _ = TryReadPatternNode(
                node: row["pattern"],
                pattern: out var pattern
            );
            _ = PatternSpelling.TryPrint(
                node: pattern,
                text: out var match
            );

            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"  match: {match}"
            );
            sb.AppendLine(value: "}");
        }
    }
}
