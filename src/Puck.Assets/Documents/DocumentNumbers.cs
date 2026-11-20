using System.Text.Json.Nodes;
using Puck.Maths;

namespace Puck.Assets.Documents;

/// <summary>Exact reads, ordering and hashing for the numbers a document tree holds.</summary>
/// <remarks>A number is held one of three ways: a signed 64-bit integer, a decimal an author wrote, or a computed
/// double. Each is an exact rational, and ordering, equality and hashing are decided on that rational, so what two
/// numbers compare as never depends on which way either is held.</remarks>
public static class DocumentNumbers {
    /// <summary>Reads a number as the exact rational it is: an integer or decimal held exactly, else a finite
    /// double.</summary>
    /// <param name="node">The numeric value.</param>
    /// <param name="number">The value.</param>
    /// <returns><see langword="true"/> when the node holds a finite number.</returns>
    public static bool TryRational(JsonNode? node, out Rational number) {
        number = default;

        if (TryExact(
            node: node,
            number: out var exact
        )) {
            number = Rational.FromDecimal(value: exact);

            return true;
        }
        if (
            (node is JsonValue value) &&
            value.TryGetValue<double>(value: out var real) &&
            double.IsFinite(d: real)
        ) {
            number = Rational.FromDouble(value: real);

            return true;
        }

        return false;
    }

    /// <summary>Compares two numeric values exactly, however each is held.</summary>
    /// <param name="left">The left numeric value.</param>
    /// <param name="right">The right numeric value.</param>
    /// <returns>The numeric ordering. A value that is not a finite number orders as zero.</returns>
    public static int Compare(JsonNode? left, JsonNode? right) {
        if (
            TryInteger(
                node: left,
                number: out var first
            ) &&
            TryInteger(
                node: right,
                number: out var second
            )
        ) {
            return first.CompareTo(value: second);
        }

        // Two values held the same way order exactly in that representation; only a decimal against a double needs
        // the rational both are.
        if (
            TryExact(
                node: left,
                number: out var leftExact
            ) &&
            TryExact(
                node: right,
                number: out var rightExact
            )
        ) {
            return leftExact.CompareTo(value: rightExact);
        }
        if (
            (left is JsonValue leftValue) &&
            (right is JsonValue rightValue) &&
            leftValue.TryGetValue<double>(value: out var leftReal) &&
            rightValue.TryGetValue<double>(value: out var rightReal) &&
            double.IsFinite(d: leftReal) &&
            double.IsFinite(d: rightReal)
        ) {
            return leftReal.CompareTo(value: rightReal);
        }

        _ = TryRational(
            node: left,
            number: out var leftRatio
        );
        _ = TryRational(
            node: right,
            number: out var rightRatio
        );

        return leftRatio.CompareTo(other: rightRatio);
    }
    /// <summary>Returns a hash that agrees with <see cref="Compare"/>: two numbers that compare equal hash equal,
    /// however each is held.</summary>
    /// <param name="node">The numeric value.</param>
    /// <param name="hash">The hash.</param>
    /// <returns><see langword="true"/> when the node holds a finite number.</returns>
    public static bool TryHash(JsonNode? node, out int hash) {
        if (TryInteger(
            node: node,
            number: out var integer
        )) {
            hash = integer.GetHashCode();

            return true;
        }
        if (TryRational(
            node: node,
            number: out var ratio
        )) {
            hash = HashCode.Combine(
                value1: ratio.Numerator,
                value2: ratio.Denominator
            );

            return true;
        }

        hash = 0;

        return false;
    }
    /// <summary>Reads a number held exactly: an integer, or a decimal an author wrote and exact arithmetic
    /// kept.</summary>
    /// <param name="node">The numeric value.</param>
    /// <param name="number">The value.</param>
    /// <returns><see langword="true"/> when the node holds an integer or a decimal. A computed
    /// <see cref="double"/> returns <see langword="false"/>.</returns>
    public static bool TryExact(JsonNode? node, out decimal number) {
        number = 0m;

        if (node is not JsonValue value) {
            return false;
        }
        if (value.TryGetValue<long>(value: out var whole)) {
            number = whole;

            return true;
        }
        if (value.TryGetValue<int>(value: out var narrow)) {
            number = narrow;

            return true;
        }

        return value.TryGetValue<decimal>(value: out number);
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
