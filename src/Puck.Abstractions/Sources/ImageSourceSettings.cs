using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Puck.Abstractions.Sources;

/// <summary>A source's settings object: the members its producer binds, as the document spelled them. Two sources of one
/// producer are the same image exactly when their settings have one canonical form (<see cref="Canonical"/>), so a
/// document's producer source, the render-graph instance that shows it and that instance's name
/// (<see cref="Digest"/>) all compare settings through this one rule.</summary>
public static class ImageSourceSettings {
    /// <summary>The number of lowercase hexadecimal characters in a <see cref="Digest"/>.</summary>
    public const int DigestLength = 16;

    private static readonly JsonWriterOptions WriterOptions = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Writes a JSON number as its value rather than its spelling: an optional minus sign, the significant digits with no
    // leading or trailing zero, and a decimal exponent when it is not zero. Every spelling of one value, such as 96, 96.0,
    // 9.6e1 and 960e-1, writes alike, and zero writes as 0 whatever its sign.
    private static void WriteNumber(Utf8JsonWriter writer, ReadOnlySpan<byte> raw) {
        var position = 0;
        var negative = (raw[0] == ((byte)'-'));

        if (negative) {
            position++;
        }

        var digits = new StringBuilder();
        var fractionDigits = 0;

        for (; ((position < raw.Length) && char.IsAsciiDigit(c: ((char)raw[position]))); position++) {
            _ = digits.Append(value: ((char)raw[position]));
        }
        if (
            (position < raw.Length) &&
            (raw[position] == ((byte)'.'))
        ) {
            for (position++; ((position < raw.Length) && char.IsAsciiDigit(c: ((char)raw[position]))); position++) {
                _ = digits.Append(value: ((char)raw[position]));
                fractionDigits++;
            }
        }

        var exponent = ((position < raw.Length)
            ? BigInteger.Parse(
                provider: CultureInfo.InvariantCulture,
                style: NumberStyles.AllowLeadingSign,
                value: Encoding.ASCII.GetString(bytes: raw[(position + 1)..])
            )
            : BigInteger.Zero
        );
        var text = digits.ToString().TrimStart(trimChar: '0');
        var significant = text.TrimEnd(trimChar: '0');

        if (significant.Length == 0) {
            writer.WriteRawValue(json: "0");

            return;
        }

        exponent += ((text.Length - significant.Length) - fractionDigits);
        writer.WriteRawValue(json: string.Concat(
            str0: (negative ? "-" : ""),
            str1: significant,
            str2: (exponent.IsZero ? "" : ("e" + exponent.ToString(provider: CultureInfo.InvariantCulture)))
        ));
    }
    private static void WriteValue(Utf8JsonWriter writer, JsonElement value) {
        switch (value.ValueKind) {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                // A stable sort, so members that share a name keep their document order.
                foreach (var member in value.EnumerateObject().OrderBy(
                    comparer: StringComparer.Ordinal,
                    keySelector: static member => member.Name
                )) {
                    writer.WritePropertyName(propertyName: member.Name);
                    WriteValue(
                        value: member.Value,
                        writer: writer
                    );
                }

                writer.WriteEndObject();

                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in value.EnumerateArray()) {
                    WriteValue(
                        value: item,
                        writer: writer
                    );
                }

                writer.WriteEndArray();

                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value: value.GetString());

                break;
            case JsonValueKind.Number:
                WriteNumber(
                    raw: JsonMarshal.GetRawUtf8Value(element: value),
                    writer: writer
                );

                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(value: (value.ValueKind == JsonValueKind.True));

                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();

                break;
            default:
                throw new ArgumentException(message: "A settings value is undefined; every member holds a JSON value.");
        }
    }

    /// <summary>Returns a settings object's canonical form: compact JSON whose members are sorted by ordinal name at every
    /// depth, whose strings are written from their values rather than their escapes, and whose numbers are written from
    /// their values (<c>96</c>, <c>96.0</c> and <c>9.6e1</c> alike). An absent object and an empty one are both
    /// <c>{}</c>. Array order is kept. Every value <see cref="JsonElement.DeepEquals"/> calls equal has one canonical
    /// form.</summary>
    /// <param name="settings">The settings object, or <see langword="null"/> for none.</param>
    /// <returns>The canonical form.</returns>
    /// <exception cref="ArgumentException">A member holds an undefined value (<see cref="JsonValueKind.Undefined"/>).</exception>
    public static string Canonical(IReadOnlyDictionary<string, JsonElement>? settings) {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(
            bufferWriter: buffer,
            options: WriterOptions
        )) {
            writer.WriteStartObject();

            if (settings is not null) {
                foreach (var (name, value) in settings.OrderBy(
                    comparer: StringComparer.Ordinal,
                    keySelector: static member => member.Key
                )) {
                    writer.WritePropertyName(propertyName: name);
                    WriteValue(
                        value: value,
                        writer: writer
                    );
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(bytes: buffer.WrittenSpan);
    }
    /// <summary>Returns a short digest of a settings object's canonical form (<see cref="Canonical"/>): the first
    /// <see cref="DigestLength"/> lowercase hexadecimal characters of its SHA-256, so equal settings digest alike and
    /// settings that differ digest differently but for a 64-bit collision.</summary>
    /// <param name="settings">The settings object, or <see langword="null"/> for none.</param>
    /// <returns>The digest.</returns>
    /// <exception cref="ArgumentException">A member holds an undefined value.</exception>
    public static string Digest(IReadOnlyDictionary<string, JsonElement>? settings) => Convert.ToHexStringLower(
        inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: Canonical(settings: settings))),
        length: (DigestLength / 2),
        offset: 0
    );
    /// <summary>Returns whether two settings objects have one canonical form (<see cref="Canonical"/>): the same members
    /// with equal values, whatever their order and spelling, an absent object equal to an empty one.</summary>
    /// <param name="left">The first settings object, or <see langword="null"/> for none.</param>
    /// <param name="right">The second settings object, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> when the two are equal.</returns>
    /// <exception cref="ArgumentException">A member holds an undefined value.</exception>
    public static bool Equal(IReadOnlyDictionary<string, JsonElement>? left, IReadOnlyDictionary<string, JsonElement>? right) {
        if (ReferenceEquals(
            objA: left,
            objB: right
        )) {
            return true;
        }

        var count = (left?.Count ?? 0);

        if (count != (right?.Count ?? 0)) {
            return false;
        }

        return (
            (count == 0) ||
            string.Equals(
                a: Canonical(settings: left),
                b: Canonical(settings: right),
                comparisonType: StringComparison.Ordinal
            )
        );
    }
    /// <summary>Returns a hash consistent with <see cref="Equal"/>: the hash of the canonical form, so equal settings hash
    /// alike.</summary>
    /// <param name="settings">The settings object, or <see langword="null"/> for none.</param>
    /// <returns>The hash.</returns>
    /// <exception cref="ArgumentException">A member holds an undefined value.</exception>
    public static int HashOf(IReadOnlyDictionary<string, JsonElement>? settings) => StringComparer.Ordinal.GetHashCode(obj: Canonical(settings: settings));
}
