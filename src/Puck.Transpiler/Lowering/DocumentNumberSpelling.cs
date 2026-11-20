using System.Text.Json.Nodes;
using Puck.Maths;
using Puck.State;

namespace Puck.Transpiler.Lowering;

/// <summary>Exact arithmetic and the one spelling of a lowered number; reading and comparing a number is
/// <see cref="Puck.Assets.Documents.DocumentNumbers"/>.</summary>
public static class DocumentNumberSpelling {
    /// <summary>Computes a sum, a difference, a product or a quotient of two exact numbers, when a decimal holds the
    /// result exactly.</summary>
    /// <param name="operation">One of <see cref="ExpressionOp.Add"/>, <see cref="ExpressionOp.Subtract"/>,
    /// <see cref="ExpressionOp.Multiply"/> and <see cref="ExpressionOp.Divide"/>.</param>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="result">The result.</param>
    /// <returns><see langword="false"/> when the result overflows a decimal or a decimal would round it: a product
    /// too small for its scale, and a quotient with no finite decimal (<c>1 / 3</c>) or more significant digits than
    /// a decimal carries. Decimal arithmetic rounds without saying so, and a rounded result is not an exact one. A
    /// zero divisor has no quotient and returns <see langword="false"/> too.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is not a sum, a difference, a
    /// product or a quotient.</exception>
    public static bool TryExactArithmetic(ExpressionOp operation, decimal left, decimal right, out decimal result) {
        if ((operation == ExpressionOp.Divide) && (right == 0m)) {
            result = 0m;

            return false;
        }

        try {
            result = (operation switch {
                ExpressionOp.Add => (left + right),
                ExpressionOp.Subtract => (left - right),
                ExpressionOp.Multiply => (left * right),
                ExpressionOp.Divide => (left / right),
                _ => throw new ArgumentOutOfRangeException(
                    actualValue: operation,
                    message: "Exact arithmetic is a sum, a difference, a product or a quotient.",
                    paramName: nameof(operation)
                ),
            });
        } catch (OverflowException) {
            result = 0m;

            return false;
        }

        var first = Rational.FromDecimal(value: left);
        var second = Rational.FromDecimal(value: right);
        var expected = (operation switch {
            ExpressionOp.Add => (first + second),
            ExpressionOp.Subtract => (first - second),
            ExpressionOp.Multiply => (first * second),
            _ => (first / second),
        });

        return (Rational.FromDecimal(value: result) == expected);
    }
    /// <summary>Returns the node an exact number lowers to: an integer when it is one a signed 64-bit value holds,
    /// and otherwise the decimal with no trailing zeros, so one value has one spelling.</summary>
    /// <param name="number">The number.</param>
    /// <returns>The node.</returns>
    public static JsonNode ExactNode(decimal number) {
        if (
            (number >= long.MinValue) &&
            (number <= long.MaxValue) &&
            (decimal.Truncate(d: number) == number)
        ) {
            return JsonValue.Create(value: ((long)number));
        }

        // Dividing by one written to the full scale drops the trailing zeros and keeps the value.
        return JsonValue.Create(value: (number / 1.0000000000000000000000000000m));
    }
    /// <summary>Returns the node a lowered number is written as, so one value has one spelling in a document however
    /// it was reached. A computed double writes its shortest round-trip digits, as a literal of those digits does,
    /// except at the small and large magnitudes where it would take an exponent; there it is written as the decimal
    /// those digits spell (<c>1 / 3 / 100000</c> writes a plain decimal, as a literal of its digits does).</summary>
    /// <param name="node">The lowered node.</param>
    /// <returns>A decimal node holding the same digits, or <paramref name="node"/> itself when it holds no computed
    /// double, its double already writes without an exponent, or a <see cref="decimal"/> cannot hold every digit of
    /// it.</returns>
    /// <remarks>Only the written spelling changes; the value read back from it is the same double. A value parsed from
    /// JSON is authored data and keeps its text.</remarks>
    public static JsonNode? Canonical(JsonNode? node) {
        if ((node is not JsonValue value) || value.TryGetValue<System.Text.Json.JsonElement>(value: out _) ||
            !value.TryGetValue<double>(value: out var real) || !double.IsFinite(d: real)) {
            return node;
        }
        var shortest = real.ToString(
            format: "R",
            provider: System.Globalization.CultureInfo.InvariantCulture
        );

        return ((shortest.Contains(value: 'E', comparisonType: StringComparison.Ordinal) && DecimalValues.TryParseEvery(number: out var spelled, text: shortest))
            ? JsonValue.Create(value: spelled)
            : node
        );
    }
}
