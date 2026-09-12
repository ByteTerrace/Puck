using System.Text.Json.Nodes;

namespace Puck.Transpiler.Lowering;

/// <summary>
/// A total order over lowered JSON values, so <c>sort</c> has ONE answer for any array an author hands it rather
/// than an answer that depends on which comparison happened to run.
/// <para>Numbers sort before strings, strings before booleans, and everything else (arrays, objects, absence) sorts
/// last in its own stable position — a document sorting those means the key projection is wrong, and a stable
/// no-op is a better answer there than a refusal that fires only on the second element.</para>
/// </summary>
public sealed class DocumentValueComparer : IComparer<JsonNode?> {
    /// <summary>The shared instance.</summary>
    public static DocumentValueComparer Instance { get; } = new();

    private DocumentValueComparer() {
    }

    /// <summary>Orders two lowered values.</summary>
    /// <param name="x">The left value.</param>
    /// <param name="y">The right value.</param>
    /// <returns>A negative number, zero, or a positive number.</returns>
    public int Compare(JsonNode? x, JsonNode? y) {
        var left = Rank(node: x);
        var right = Rank(node: y);

        if (left != right) {
            return left.CompareTo(value: right);
        }

        if (left == 0) {
            return DocumentNumbers.Compare(x, y);
        }

        if ((left == 1) && (x is JsonValue xValue) && (y is JsonValue yValue) &&
            xValue.TryGetValue<string>(value: out var xText) && yValue.TryGetValue<string>(value: out var yText)) {
            return string.CompareOrdinal(strA: xText, strB: yText);
        }

        if ((left == 2) && (x is JsonValue xFlag) && (y is JsonValue yFlag) &&
            xFlag.TryGetValue<bool>(value: out var xBool) && yFlag.TryGetValue<bool>(value: out var yBool)) {
            return xBool.CompareTo(value: yBool);
        }

        return 0;
    }

    private static int Rank(JsonNode? node) {
        if (node is not JsonValue value) {
            return 3;
        }

        if (DocumentLowering.TryReadNumber(node: node, number: out _)) {
            return 0;
        }

        if (value.TryGetValue<string>(value: out _)) {
            return 1;
        }

        return value.TryGetValue<bool>(value: out _) ? 2 : 3;
    }
}
