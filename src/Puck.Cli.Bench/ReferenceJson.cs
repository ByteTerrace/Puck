using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Maths;

namespace Puck.Cli.Bench;

/// <summary>Writes the evidence manifest's JSON in the committed layout: every node small enough to read as one row
/// is inlined, so the file reads as a table of evidence rather than a tower of braces.</summary>
internal static class ReferenceJson {
    private const int InlineWidth = 400;

    /// <summary>Returns a two-element rational array, the shape the manifest stores every exact ratio in.</summary>
    /// <param name="value">The rational.</param>
    public static JsonArray Ratio(Rational value) => [((long)value.Numerator), ((long)value.Denominator)];
    /// <summary>Returns the manifest text for one node, ending in a newline.</summary>
    /// <param name="node">The manifest's root.</param>
    public static string Render(JsonNode node) {
        var builder = new StringBuilder();

        Render(
            builder: builder,
            depth: 0,
            node: node
        );
        return (builder.Append(value: '\n').ToString());
    }

    private static void Compact(StringBuilder builder, JsonNode? node) {
        switch (node) {
            case JsonObject members: {
                    var first = true;

                    builder.Append(value: '{');
                    foreach (var (name, value) in members) {
                        if (!first) { builder.Append(value: ", "); }
                        first = false;
                        Text(
                            builder: builder,
                            value: name
                        );
                        builder.Append(value: ": ");
                        Compact(
                            builder: builder,
                            node: value
                        );
                    }
                    builder.Append(value: '}');
                    return;
                }
            case JsonArray entries: {
                    var first = true;

                    builder.Append(value: '[');
                    foreach (var value in entries) {
                        if (!first) { builder.Append(value: ", "); }
                        first = false;
                        Compact(
                            builder: builder,
                            node: value
                        );
                    }
                    builder.Append(value: ']');
                    return;
                }
            case JsonValue value when value.TryGetValue(value: out string? text): {
                    Text(
                        builder: builder,
                        value: text
                    );
                    return;
                }
            default: {
                    builder.Append(value: (node?.ToJsonString() ?? "null"));
                    return;
                }
        }
    }
    private static void Render(StringBuilder builder, JsonNode? node, int depth) {
        var compact = new StringBuilder();

        Compact(
            builder: compact,
            node: node
        );
        if ((compact.Length <= InlineWidth) || (node is not (JsonObject or JsonArray))) {
            builder.Append(value: compact);
            return;
        }

        var close = new string(
            c: ' ',
            count: (4 * depth)
        );
        var pad = (close + "    ");
        var first = true;

        if (node is JsonObject members) {
            builder.Append(value: "{\n");
            foreach (var (name, value) in members) {
                if (!first) { builder.Append(value: ",\n"); }
                first = false;
                builder.Append(value: pad);
                Text(
                    builder: builder,
                    value: name
                );
                builder.Append(value: ": ");
                Render(
                    builder: builder,
                    depth: (depth + 1),
                    node: value
                );
            }
            builder.Append(value: '\n').Append(value: close).Append(value: '}');
            return;
        }
        builder.Append(value: "[\n");
        foreach (var value in ((JsonArray)node!)) {
            if (!first) { builder.Append(value: ",\n"); }
            first = false;
            builder.Append(value: pad);
            Render(
                builder: builder,
                depth: (depth + 1),
                node: value
            );
        }
        builder.Append(value: '\n').Append(value: close).Append(value: ']');
    }
    private static void Text(StringBuilder builder, string value) {
        builder.Append(value: '"');
        foreach (var character in value) {
            switch (character) {
                case '"': { builder.Append(value: "\\\""); break; }
                case '\\': { builder.Append(value: "\\\\"); break; }
                case '\b': { builder.Append(value: "\\b"); break; }
                case '\f': { builder.Append(value: "\\f"); break; }
                case '\n': { builder.Append(value: "\\n"); break; }
                case '\r': { builder.Append(value: "\\r"); break; }
                case '\t': { builder.Append(value: "\\t"); break; }
                default: {
                        if ((character < ' ') || (character > '~')) {
                            builder.Append(value: "\\u").Append(value: ((int)character).ToString(
                                format: "x4",
                                provider: CultureInfo.InvariantCulture
                            ));
                            break;
                        }
                        builder.Append(value: character);
                        break;
                    }
            }
        }
        builder.Append(value: '"');
    }
}
