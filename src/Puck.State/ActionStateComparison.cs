using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.State;

/// <summary>A fixed comparison admitted by a compiled state predicate.</summary>
[JsonConverter(typeof(StrictEnumConverter<ActionStateComparison>))]
public enum ActionStateComparison : byte {
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}
/// <summary>The one evaluation of an <see cref="ActionStateComparison"/> — a kit action's own state predicate and a
/// state rule's <c>compareState</c> operand ask exactly the same question of the same fixed-point pair, so the
/// vocabulary is decided in one place and neither can grow an arm the other lacks.</summary>
public static class ActionStateComparisons {
    /// <summary>Evaluates the comparison against a value/expectation pair.</summary>
    /// <param name="comparison">The comparison to evaluate.</param>
    /// <param name="value">The observed value.</param>
    /// <param name="expected">The value compared against.</param>
    /// <returns><see langword="true"/> when the comparison holds.</returns>
    public static bool Holds(this ActionStateComparison comparison, FixedQ4816 value, FixedQ4816 expected) => comparison switch {
        ActionStateComparison.Equal => (value == expected),
        ActionStateComparison.NotEqual => (value != expected),
        ActionStateComparison.Less => (value < expected),
        ActionStateComparison.LessOrEqual => (value <= expected),
        ActionStateComparison.Greater => (value > expected),
        _ => (value >= expected),
    };
    /// <summary>Evaluates the comparison when either side may be positive infinity — a fact whose magnitude exceeds
    /// every representable number (a host's "forever" channel). Infinity compares as strictly greater than every
    /// finite value and equal to itself, so <c>&gt; finite</c> holds, <c>&lt;= finite</c> does not, and
    /// <c>== finite</c> never does. A sentinel numeric encoding was deliberately rejected: any finite stand-in is a
    /// value an authored comparand could legitimately equal, and a comparison that cannot distinguish "forever" from
    /// one particular number is lying about one of them.</summary>
    /// <param name="comparison">The comparison to evaluate.</param>
    /// <param name="value">The observed value; ignored when <paramref name="valueIsForever"/>.</param>
    /// <param name="valueIsForever">Whether the observed side is positive infinity.</param>
    /// <param name="expected">The value compared against; ignored when <paramref name="expectedIsForever"/>.</param>
    /// <param name="expectedIsForever">Whether the expected side is positive infinity.</param>
    /// <returns><see langword="true"/> when the comparison holds.</returns>
    public static bool Holds(this ActionStateComparison comparison, FixedQ4816 value, bool valueIsForever, FixedQ4816 expected, bool expectedIsForever) {
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
            ActionStateComparison.Equal => (sign == 0),
            ActionStateComparison.NotEqual => (sign != 0),
            ActionStateComparison.Less => (sign < 0),
            ActionStateComparison.LessOrEqual => (sign <= 0),
            ActionStateComparison.Greater => (sign > 0),
            _ => (sign >= 0),
        };
    }
}
