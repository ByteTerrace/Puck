using System.Globalization;

namespace Puck.World;

public readonly partial record struct WorldValueDomainDiagnostic {
    /// <summary>Writes the same invariant diagnostic the console reports into caller-owned storage.</summary>
    /// <param name="destination">The available character storage.</param>
    /// <param name="charsWritten">The written length, or zero when the destination is too small.</param>
    /// <param name="format">Unused; diagnostics have one shared format.</param>
    /// <param name="provider">Unused; diagnostic values always use invariant culture.</param>
    /// <returns>Whether the complete diagnostic fits.</returns>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) {
        if (Values is not null) {
            return destination.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out charsWritten,
                handler: $"{Field} from {Source}: {Values} requires {Domain}; {Action} {UsedValues}");
        }
        if (ThirdValue is { } third) {
            Span<char> optional = stackalloc char[96];
            var second = Optional(value: SecondValue, chars: optional[..32]);
            var secondUsed = Optional(value: SecondUsed, chars: optional[32..64]);
            var thirdUsed = Optional(value: ThirdUsed, chars: optional[64..]);

            return destination.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out charsWritten,
                handler: $"{Field} from {Source}: ({Value}, {second}, {third}) requires {Domain}; {Action} ({Used}, {secondUsed}, {thirdUsed})");
        }
        if (SecondValue is { } pair) {
            Span<char> optional = stackalloc char[32];
            var secondUsed = Optional(value: SecondUsed, chars: optional);

            return destination.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out charsWritten,
                handler: $"{Field} from {Source}: ({Value}, {pair}) requires {Domain}; {Action} ({Used}, {secondUsed})");
        }
        return destination.TryWrite(provider: CultureInfo.InvariantCulture, charsWritten: out charsWritten,
            handler: $"{Field} from {Source}: {Value} requires {Domain}; {Action} {Used}");
    }
    /// <inheritdoc/>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    private static ReadOnlySpan<char> Optional(double? value, Span<char> chars) {
        if (value is not { } number) { return []; }
        // The invariant general form of a double fits in the caller's 32 characters.
        _ = number.TryFormat(destination: chars, charsWritten: out var length, provider: CultureInfo.InvariantCulture);
        return chars[..length];
    }
}
