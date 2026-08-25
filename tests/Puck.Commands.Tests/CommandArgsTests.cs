using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Pins the single console argument-parse rule: invariant culture, finite floats, plain integers, with the
/// <see cref="string"/> and <see cref="System.ReadOnlySpan{T}"/> overloads answering identically.</summary>
public sealed class CommandArgsTests {
    [InlineData("1.5", true, 1.5f)]
    [InlineData("-2", true, -2f)]
    [InlineData("0", true, 0f)]
    [InlineData("1e2", true, 100f)]
    [InlineData(" 1.5 ", true, 1.5f)]
    [InlineData("abc", false, 0f)]
    [InlineData("", false, 0f)]
    [InlineData("NaN", false, 0f)]
    [InlineData("Infinity", false, 0f)]
    [InlineData("1,5", false, 0f)]
    [Theory]
    public void TryParseFloatFollowsTheInvariantFiniteRule(string text, bool expected, float value) {
        Assert.Equal(expected: expected, actual: CommandArgs.TryParseFloat(text: text, value: out var parsed));

        if (expected) {
            Assert.Equal(actual: parsed, expected: value);
        }
    }
    [InlineData("42", true, 42)]
    [InlineData("-3", true, -3)]
    [InlineData("1.5", false, 0)]
    [InlineData("abc", false, 0)]
    [Theory]
    public void TryParseIntFollowsTheInvariantIntegerRule(string text, bool expected, int value) {
        Assert.Equal(expected: expected, actual: CommandArgs.TryParseInt(text: text, value: out var parsed));

        if (expected) {
            Assert.Equal(actual: parsed, expected: value);
        }
    }
    [InlineData("1.5")]
    [InlineData("NaN")]
    [InlineData("-2")]
    [InlineData("nonsense")]
    [Theory]
    public void SpanAndStringOverloadsAgree(string text) {
        var stringOk = CommandArgs.TryParseFloat(text: text, value: out var fromString);
        var spanOk = CommandArgs.TryParseFloat(text: text.AsSpan(), value: out var fromSpan);

        Assert.Equal(actual: spanOk, expected: stringOk);
        Assert.Equal(actual: fromSpan, expected: fromString);
    }
    [Fact]
    public void TryParseFloatsParsesAConsecutiveRunOrFailsAsAUnit() {
        Assert.True(condition: CommandArgs.TryParseFloats(args: ["9", "1", "2", "3"], count: 3, start: 1, out var values));
        Assert.Equal(actual: values, expected: new[] { 1f, 2f, 3f });

        // A missing final token fails the whole run rather than partially parsing.
        Assert.False(condition: CommandArgs.TryParseFloats(args: ["1", "2"], count: 3, start: 0, out _));
    }
    [InlineData("42", true, 42L)]
    [InlineData("-3", true, -3L)]
    [InlineData("9223372036854775807", true, long.MaxValue)]
    [InlineData("1.5", false, 0L)]
    [InlineData("abc", false, 0L)]
    [InlineData("", false, 0L)]
    [Theory]
    public void TryParseLongFollowsTheInvariantIntegerRule(string text, bool expected, long value) {
        Assert.Equal(expected: expected, actual: CommandArgs.TryParseLong(text: text, value: out var parsed));

        if (expected) {
            Assert.Equal(actual: parsed, expected: value);
        }
    }
    [InlineData("42", true, 42UL)]
    [InlineData("0", true, 0UL)]
    [InlineData("18446744073709551615", true, ulong.MaxValue)]
    [InlineData("-1", false, 0UL)]
    [InlineData("1.5", false, 0UL)]
    [InlineData("abc", false, 0UL)]
    [Theory]
    public void TryParseULongFollowsTheInvariantIntegerRule(string text, bool expected, ulong value) {
        Assert.Equal(expected: expected, actual: CommandArgs.TryParseULong(text: text, value: out var parsed));

        if (expected) {
            Assert.Equal(actual: parsed, expected: value);
        }
    }
    [InlineData("1.5")]
    [InlineData("-2")]
    [InlineData("nonsense")]
    [Theory]
    public void LongSpanAndStringOverloadsAgree(string text) {
        var stringOk = CommandArgs.TryParseLong(text: text, value: out var fromString);
        var spanOk = CommandArgs.TryParseLong(text: text.AsSpan(), value: out var fromSpan);

        Assert.Equal(actual: spanOk, expected: stringOk);
        Assert.Equal(actual: fromSpan, expected: fromString);
    }
}
