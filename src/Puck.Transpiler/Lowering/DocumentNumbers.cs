using System.Numerics;
using System.Text.Json.Nodes;

namespace Puck.Transpiler.Lowering;

/// <summary>Exact reads, ordering, hashing and arithmetic for compile-time numbers.</summary>
/// <remarks>A lowered number is held one of three ways: a signed 64-bit integer, a decimal an author wrote, or a
/// computed double. Each is an exact rational, and ordering, equality and hashing are decided on that rational, so
/// what two numbers compare as never depends on which way either is held.</remarks>
public static class DocumentNumbers {
    // A number as the ratio it is, in lowest terms with a positive denominator, so equal values are equal fields.
    private readonly record struct Rational(BigInteger Numerator, BigInteger Denominator) {
        public static Rational Of(BigInteger numerator, BigInteger denominator) {
            var divisor = BigInteger.GreatestCommonDivisor(
                left: numerator,
                right: denominator
            );

            return (divisor.IsZero
                ? new Rational(
                    Denominator: BigInteger.One,
                    Numerator: BigInteger.Zero
                )
                : new Rational(
                    Denominator: (denominator / divisor),
                    Numerator: (numerator / divisor)
                )
            );
        }
        public static Rational Of(decimal value) {
            Span<int> bits = stackalloc int[4];

            _ = decimal.GetBits(
                d: value,
                destination: bits
            );

            var magnitude = ((((BigInteger)((uint)bits[2])) << 64) | (((BigInteger)((uint)bits[1])) << 32) | ((uint)bits[0]));

            return Of(
                denominator: BigInteger.Pow(
                    exponent: ((bits[3] >> 16) & 0xFF),
                    value: 10
                ),
                numerator: ((bits[3] < 0)
                    ? -magnitude
                    : magnitude
                )
            );
        }
        // IEEE 754 binary64: a sign, eleven exponent bits biased by 1023, and fifty-two fraction bits. A finite
        // value is its significand times a power of two, exactly.
        public static Rational Of(double value) {
            var bits = BitConverter.DoubleToInt64Bits(value: value);
            var biased = ((int)((bits >> 52) & 0x7FF));
            var fraction = (bits & 0xFFFFFFFFFFFFFL);
            var significand = ((BigInteger)((biased == 0)
                ? fraction
                : (fraction | (1L << 52))
            ));
            var exponent = (((biased == 0)
                ? 1
                : biased
            ) - 1075);

            if (bits < 0) {
                significand = -significand;
            }

            return ((exponent >= 0)
                ? Of(
                    denominator: BigInteger.One,
                    numerator: (significand << exponent)
                )
                : Of(
                    denominator: (BigInteger.One << -exponent),
                    numerator: significand
                )
            );
        }

        public int CompareTo(Rational other) => (Numerator * other.Denominator).CompareTo(other: (other.Numerator * Denominator));
    }

    private static bool TryRational(JsonNode? node, out Rational number) {
        number = default;

        if (TryExact(
            node: node,
            number: out var exact
        )) {
            number = Rational.Of(value: exact);

            return true;
        }
        if (
            (node is JsonValue value) &&
            value.TryGetValue<double>(value: out var real) &&
            double.IsFinite(d: real)
        ) {
            number = Rational.Of(value: real);

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

        _ = TryRational(
            node: left,
            number: out var leftRatio
        );
        _ = TryRational(
            node: right,
            number: out var rightRatio
        );

        if (leftRatio.Denominator.IsZero) {
            leftRatio = Rational.Of(value: 0m);
        }
        if (rightRatio.Denominator.IsZero) {
            rightRatio = Rational.Of(value: 0m);
        }

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
    /// <summary>Computes a sum, a difference or a product of two exact numbers, when a decimal holds the result
    /// exactly.</summary>
    /// <param name="operation">One of <c>+</c>, <c>-</c> and <c>*</c>.</param>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="result">The result.</param>
    /// <returns><see langword="false"/> when the result overflows a decimal or a decimal would round it, a product
    /// too small for its scale included: decimal arithmetic rounds without saying so, and a rounded result is not
    /// an exact one.</returns>
    public static bool TryExactArithmetic(string operation, decimal left, decimal right, out decimal result) {
        ArgumentNullException.ThrowIfNull(argument: operation);

        try {
            result = (operation switch {
                "+" => (left + right),
                "-" => (left - right),
                "*" => (left * right),
                _ => throw new ArgumentOutOfRangeException(
                    actualValue: operation,
                    message: "Exact arithmetic is a sum, a difference or a product.",
                    paramName: nameof(operation)
                ),
            });
        } catch (OverflowException) {
            result = 0m;

            return false;
        }

        var first = Rational.Of(value: left);
        var second = Rational.Of(value: right);
        var expected = (operation switch {
            "+" => Rational.Of(
                denominator: (first.Denominator * second.Denominator),
                numerator: ((first.Numerator * second.Denominator) + (second.Numerator * first.Denominator))
            ),
            "-" => Rational.Of(
                denominator: (first.Denominator * second.Denominator),
                numerator: ((first.Numerator * second.Denominator) - (second.Numerator * first.Denominator))
            ),
            _ => Rational.Of(
                denominator: (first.Denominator * second.Denominator),
                numerator: (first.Numerator * second.Numerator)
            ),
        });

        return (Rational.Of(value: result) == expected);
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
