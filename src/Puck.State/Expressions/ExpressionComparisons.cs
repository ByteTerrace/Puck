using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.State;

/// <summary>The comparison subset of <see cref="ExpressionOp"/> — <see cref="ExpressionOp.Equal"/>,
/// <see cref="ExpressionOp.NotEqual"/>, <see cref="ExpressionOp.Less"/>, <see cref="ExpressionOp.LessOrEqual"/>,
/// <see cref="ExpressionOp.Greater"/>, and <see cref="ExpressionOp.GreaterOrEqual"/> — and the one place a comparison
/// is flipped, spelled, and evaluated. A kit action's state predicate, a rule's gate, a field condition, and an
/// expression's comparison opcode are the same six operations, so each question has one answer.</summary>
public static class ExpressionComparisons {
    /// <summary>Gets the six comparisons, in declaration order.</summary>
    public static IReadOnlyList<ExpressionOp> All { get; } = [
        ExpressionOp.Equal,
        ExpressionOp.NotEqual,
        ExpressionOp.Less,
        ExpressionOp.LessOrEqual,
        ExpressionOp.Greater,
        ExpressionOp.GreaterOrEqual,
    ];

    /// <summary>Returns whether an operation is one of the six comparisons: its operator row reads two operands of
    /// one kind and yields Int 1 or 0.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns><see langword="true"/> for a comparison.</returns>
    public static bool IsComparison(this ExpressionOp operation) =>
        (ExpressionOperators.Find(operation: operation)?.Signature == ExpressionSignature.Comparison);
    /// <summary>Returns the comparison that holds with its operands exchanged — <c>a &lt; b</c> read as
    /// <c>b &gt; a</c>. Equality and inequality are their own flip.</summary>
    /// <param name="comparison">The comparison.</param>
    /// <returns>The flipped comparison.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="comparison"/> is not a comparison.</exception>
    public static ExpressionOp Flip(this ExpressionOp comparison) => comparison switch {
        ExpressionOp.Equal or ExpressionOp.NotEqual => comparison,
        ExpressionOp.Less => ExpressionOp.Greater,
        ExpressionOp.LessOrEqual => ExpressionOp.GreaterOrEqual,
        ExpressionOp.Greater => ExpressionOp.Less,
        ExpressionOp.GreaterOrEqual => ExpressionOp.LessOrEqual,
        _ => throw NotAComparison(operation: comparison),
    };
    /// <summary>Returns a comparison's operator spelling (<c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>,
    /// <c>&gt;</c>, <c>&gt;=</c>), read from its <see cref="ExpressionOperators"/> row.</summary>
    /// <param name="comparison">The comparison.</param>
    /// <returns>The operator symbol.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="comparison"/> is not a comparison.</exception>
    public static string Symbol(this ExpressionOp comparison) => (comparison.IsComparison()
        ? ExpressionOperators.Find(operation: comparison)!.Symbol!
        : throw NotAComparison(operation: comparison)
    );
    /// <summary>Reads a comparison by its operator spelling.</summary>
    /// <param name="symbol">The operator symbol.</param>
    /// <param name="comparison">The comparison, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="symbol"/> spells a comparison.</returns>
    public static bool TryParseSymbol(string? symbol, out ExpressionOp comparison) {
        if (
            (symbol is not null) &&
            ExpressionOperators.TryFindSymbol(
                descriptor: out var descriptor,
                symbol: symbol
            ) &&
            descriptor!.Operation.IsComparison()
        ) {
            comparison = descriptor.Operation;

            return true;
        }

        comparison = default;

        return false;
    }
    /// <summary>Reads a comparison by its document spelling: exactly its member name, in its own casing. Any other
    /// casing, a number, and the name of an operation that is not a comparison are refused, never folded onto a
    /// neighbouring member.</summary>
    /// <param name="name">The document spelling.</param>
    /// <param name="comparison">The comparison, on success.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is a comparison's member name.</returns>
    public static bool TryParseName(string? name, out ExpressionOp comparison) {
        foreach (var candidate in All) {
            if (string.Equals(
                a: Enum.GetName(value: candidate),
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                comparison = candidate;

                return true;
            }
        }

        comparison = default;

        return false;
    }
    /// <summary>Evaluates a comparison against a value/expectation pair.</summary>
    /// <param name="comparison">The comparison.</param>
    /// <param name="value">The observed value.</param>
    /// <param name="expected">The value compared against.</param>
    /// <returns><see langword="true"/> when the comparison holds.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="comparison"/> is not a comparison.</exception>
    public static bool Holds(this ExpressionOp comparison, FixedQ4816 value, FixedQ4816 expected) => comparison switch {
        ExpressionOp.Equal => (value == expected),
        ExpressionOp.NotEqual => (value != expected),
        ExpressionOp.Less => (value < expected),
        ExpressionOp.LessOrEqual => (value <= expected),
        ExpressionOp.Greater => (value > expected),
        ExpressionOp.GreaterOrEqual => (value >= expected),
        _ => throw NotAComparison(operation: comparison),
    };
    /// <summary>Evaluates a comparison when either side may be positive infinity — a fact whose magnitude exceeds
    /// every representable number (a host's "forever" channel). Infinity compares as strictly greater than every
    /// finite value and equal to itself, so <c>&gt; finite</c> holds, <c>&lt;= finite</c> does not, and
    /// <c>== finite</c> never does. A sentinel numeric encoding was deliberately rejected: any finite stand-in is a
    /// value an authored comparand could legitimately equal, and a comparison that cannot distinguish "forever" from
    /// one particular number is lying about one of them.</summary>
    /// <param name="comparison">The comparison.</param>
    /// <param name="value">The observed value; ignored when <paramref name="valueIsForever"/>.</param>
    /// <param name="valueIsForever">Whether the observed side is positive infinity.</param>
    /// <param name="expected">The value compared against; ignored when <paramref name="expectedIsForever"/>.</param>
    /// <param name="expectedIsForever">Whether the expected side is positive infinity.</param>
    /// <returns><see langword="true"/> when the comparison holds.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="comparison"/> is not a comparison.</exception>
    public static bool Holds(this ExpressionOp comparison, FixedQ4816 value, bool valueIsForever, FixedQ4816 expected, bool expectedIsForever) {
        if (
            !valueIsForever &&
            !expectedIsForever
        ) {
            return comparison.Holds(
                expected: expected,
                value: value
            );
        }

        // Exactly one or both sides are infinite; the finite magnitudes no longer matter, only the ordering sign.
        var sign = ((valueIsForever, expectedIsForever)) switch {
            (true, true) => 0,
            (true, false) => 1,
            _ => -1,
        };

        return comparison switch {
            ExpressionOp.Equal => (sign == 0),
            ExpressionOp.NotEqual => (sign != 0),
            ExpressionOp.Less => (sign < 0),
            ExpressionOp.LessOrEqual => (sign <= 0),
            ExpressionOp.Greater => (sign > 0),
            ExpressionOp.GreaterOrEqual => (sign >= 0),
            _ => throw NotAComparison(operation: comparison),
        };
    }

    private static ArgumentOutOfRangeException NotAComparison(ExpressionOp operation) => new(
        actualValue: operation,
        message: $"'{operation}' is not a comparison",
        paramName: nameof(operation)
    );
}
/// <summary>A comparison as a document spells it: one of the six comparison operations of
/// <see cref="ExpressionOp"/> (<c>Equal</c>, <c>NotEqual</c>, <c>Less</c>, <c>LessOrEqual</c>, <c>Greater</c>,
/// <c>GreaterOrEqual</c>), named by its member name exactly. Another operation's name, another casing, and a number
/// are refused on read.</summary>
/// <remarks>Applied per member (<c>[property: JsonConverter(typeof(ExpressionComparisonJsonConverter))]</c>), since
/// <see cref="ExpressionOp"/>'s own converter admits every operation.</remarks>
public sealed class ExpressionComparisonJsonConverter : JsonConverter<ExpressionOp>, IJsonSchemaStringConverter {
    private static readonly string[] Names = [.. ExpressionComparisons.All.Select(selector: static comparison => Enum.GetName(value: comparison)!)];

    /// <inheritdoc/>
    public IReadOnlyList<string>? SchemaTokens => Names;

    /// <inheritdoc/>
    public override ExpressionOp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var name = ((reader.TokenType == JsonTokenType.String)
            ? reader.GetString()
            : null
        );

        return (ExpressionComparisons.TryParseName(
            comparison: out var comparison,
            name: name
        )
            ? comparison
            : throw new JsonException(message: $"A comparison is one of {string.Join(separator: ", ", value: Names)}; '{(name ?? reader.TokenType.ToString())}' is not."));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ExpressionOp value, JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        if (!value.IsComparison()) {
            throw new JsonException(message: $"'{value}' is not a comparison and has no comparison spelling.");
        }

        writer.WriteStringValue(value: Enum.GetName(value: value));
    }
}
