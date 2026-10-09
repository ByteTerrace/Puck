using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck.Cli.Bench;

/// <summary>An exact nonnegative rational, kept in lowest terms.</summary>
/// <param name="Numerator">The numerator.</param>
/// <param name="Denominator">The positive denominator.</param>
internal readonly record struct ReferenceRational(long Numerator, long Denominator) {
    /// <summary>Gets this rational rounded upward to a whole number of cycles.</summary>
    public long Ceiling => (((Numerator + Denominator) - 1L) / Denominator);

    /// <summary>Returns the exact arithmetic mean of two rationals.</summary>
    /// <param name="left">One rational.</param>
    /// <param name="right">The other.</param>
    public static ReferenceRational Mean(ReferenceRational left, ReferenceRational right) => Of(
        denominator: ((2L * left.Denominator) * right.Denominator),
        numerator: ((left.Numerator * right.Denominator) + (right.Numerator * left.Denominator))
    );
    /// <summary>Returns the equal-weight median of a sample, taking an even-sized median as the exact arithmetic mean
    /// of its middle two values.</summary>
    /// <param name="samples">The sample, which must not be empty.</param>
    public static ReferenceRational Median(IReadOnlyList<ReferenceRational> samples) {
        var ordered = samples.Order(comparer: Comparer<ReferenceRational>.Create(comparison: static (left, right) => (left.Numerator * right.Denominator).CompareTo(value: (right.Numerator * left.Denominator)))).ToArray();
        var middle = (ordered.Length / 2);

        return (((ordered.Length % 2) == 1)
            ? ordered[middle]
            : Mean(
                left: ordered[(middle - 1)],
                right: ordered[middle]
            ));
    }
    /// <summary>Returns a rational in lowest terms.</summary>
    /// <param name="numerator">The numerator.</param>
    /// <param name="denominator">The positive denominator.</param>
    public static ReferenceRational Of(long numerator, long denominator) {
        var divisor = Math.Max(
            val1: 1L,
            val2: GreatestCommonDivisor(
                left: Math.Abs(value: numerator),
                right: denominator
            )
        );

        return new(
            Denominator: (denominator / divisor),
            Numerator: (numerator / divisor)
        );
    }
    /// <summary>Returns the exact rational a decimal transcribes.</summary>
    /// <param name="value">The decimal, which the disassembly tools print with a fixed number of places.</param>
    public static ReferenceRational Of(decimal value) {
        var denominator = 1L;

        while ((decimal.Truncate(d: value) != value) && (denominator <= 1000L)) {
            denominator *= 10L;
            value *= 10M;
        }
        return Of(
            denominator: denominator,
            numerator: decimal.ToInt64(d: decimal.Round(d: value))
        );
    }

    private static long GreatestCommonDivisor(long left, long right) {
        while (right != 0L) { (left, right) = (right, (left % right)); }
        return left;
    }
}
/// <summary>Writes the evidence manifest's JSON in the committed layout: every node small enough to read as one row
/// is inlined, so the file reads as a table of evidence rather than a tower of braces.</summary>
internal static class ReferenceJson {
    private const int InlineWidth = 400;

    /// <summary>Returns a two-element rational array, the shape the manifest stores every exact ratio in.</summary>
    /// <param name="value">The rational.</param>
    public static JsonArray Rational(ReferenceRational value) => [value.Numerator, value.Denominator];
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
