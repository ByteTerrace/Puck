using System.Text.Json.Nodes;

namespace Puck.Transpiler.Lowering;

/// <summary>Exact integer reads and mixed numeric ordering for compile-time values.</summary>
public static class DocumentNumbers {
    private static int CompareInteger(long integer, double real) {
        if (real >= 9223372036854775808d) {
            return -1;
        }
        if (real < long.MinValue) {
            return 1;
        }
        var whole = ((long)real);
        var order = integer.CompareTo(value: whole);

        return ((order != 0)
            ? order
            : ((double)whole).CompareTo(value: real)
        );
    }

    /// <summary>Compares two numeric values while preserving all integer bits.</summary>
    /// <param name="left">The left numeric value.</param>
    /// <param name="right">The right numeric value.</param>
    /// <returns>The numeric ordering.</returns>
    public static int Compare(JsonNode? left, JsonNode? right) {
        var leftInteger = TryInteger(
            node: left,
            number: out var first
        );
        var rightInteger = TryInteger(
            node: right,
            number: out var second
        );

        if (
            leftInteger &&
            rightInteger
        ) {
            return first.CompareTo(value: second);
        }
        DocumentLowering.TryReadNumber(
            node: left,
            number: out var leftReal
        );
        DocumentLowering.TryReadNumber(
            node: right,
            number: out var rightReal
        );
        if (leftInteger) {
            return CompareInteger(
                integer: first,
                real: rightReal
            );
        }
        return (rightInteger
            ? -CompareInteger(
                integer: second,
                real: leftReal
            )
            : leftReal.CompareTo(value: rightReal)
        );
    }
    /// <summary>Reads a whole number without routing integer storage through floating point.</summary>
    /// <param name="node">The numeric value.</param>
    /// <param name="number">The signed 64-bit result.</param>
    /// <returns>True when the value is an exactly represented signed 64-bit integer.</returns>
    public static bool TryInteger(JsonNode? node, out long number) {
        number = 0;
        if (node is not JsonValue value) {
            return false;
        }
        if (value.TryGetValue<long>(value: out number)) {
            return true;
        }
        if (value.TryGetValue<int>(value: out var narrow)) {
            number = narrow;
            return true;
        }
        if (
            value.TryGetValue<decimal>(value: out var exact) &&
            (exact >= long.MinValue) &&
            (exact <= long.MaxValue) &&
            (decimal.Truncate(d: exact) == exact)
        ) {
            number = ((long)exact);
            return true;
        }
        if (
            value.TryGetValue<double>(value: out var real) &&
            double.IsFinite(d: real) &&
            (real >= long.MinValue) &&
            (real < 9223372036854775808d) &&
            (Math.Truncate(d: real) == real)
        ) {
            number = ((long)real);
            return true;
        }
        return false;
    }
}
