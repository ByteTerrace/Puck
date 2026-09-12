using System.Text.Json.Nodes;

namespace Puck.Transpiler.Lowering;

/// <summary>Exact integer reads and mixed numeric ordering for compile-time values.</summary>
public static class DocumentNumbers {
    /// <summary>Reads a whole number without routing integer storage through floating point.</summary>
    /// <param name="node">The numeric value.</param>
    /// <param name="number">The signed 64-bit result.</param>
    /// <returns>True when the value is an exactly represented signed 64-bit integer.</returns>
    public static bool TryInteger(JsonNode? node, out long number) {
        number = 0;
        if (node is not JsonValue value) {
            return false;
        }
        if (value.TryGetValue<long>(out number)) {
            return true;
        }
        if (value.TryGetValue<int>(out var narrow)) {
            number = narrow;
            return true;
        }
        if (value.TryGetValue<decimal>(out var exact) && exact >= long.MinValue && exact <= long.MaxValue && decimal.Truncate(exact) == exact) {
            number = (long)exact;
            return true;
        }
        if (value.TryGetValue<double>(out var real) && double.IsFinite(real) && real >= long.MinValue && real < 9223372036854775808d && Math.Truncate(real) == real) {
            number = (long)real;
            return true;
        }
        return false;
    }

    /// <summary>Compares two numeric values while preserving all integer bits.</summary>
    /// <param name="left">The left numeric value.</param>
    /// <param name="right">The right numeric value.</param>
    /// <returns>The numeric ordering.</returns>
    public static int Compare(JsonNode? left, JsonNode? right) {
        var leftInteger = TryInteger(left, out var first);
        var rightInteger = TryInteger(right, out var second);
        if (leftInteger && rightInteger) {
            return first.CompareTo(second);
        }
        DocumentLowering.TryReadNumber(left, out var leftReal);
        DocumentLowering.TryReadNumber(right, out var rightReal);
        if (leftInteger) {
            return CompareInteger(first, rightReal);
        }
        return rightInteger ? -CompareInteger(second, leftReal) : leftReal.CompareTo(rightReal);
    }

    private static int CompareInteger(long integer, double real) {
        if (real >= 9223372036854775808d) {
            return -1;
        }
        if (real < long.MinValue) {
            return 1;
        }
        var whole = (long)real;
        var order = integer.CompareTo(whole);
        return order != 0 ? order : ((double)whole).CompareTo(real);
    }
}
