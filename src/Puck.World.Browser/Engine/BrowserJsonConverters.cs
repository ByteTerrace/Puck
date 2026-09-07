using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World.Browser.Engine;

/// <summary>Reads and writes a <see langword="long"/> as a decimal string — the wire shape every 64-bit value in
/// this engine's exported JSON takes, since JavaScript's <c>Number</c> cannot hold the full range exactly.</summary>
public sealed class LongAsStringJsonConverter : JsonConverter<long> {
    /// <inheritdoc/>
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        long.Parse(s: (reader.GetString() ?? throw new JsonException(message: "expected a decimal string.")), style: NumberStyles.AllowLeadingSign, provider: CultureInfo.InvariantCulture);
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value: value.ToString(provider: CultureInfo.InvariantCulture));
}
/// <summary>The <see langword="ulong"/> twin of <see cref="LongAsStringJsonConverter"/>.</summary>
public sealed class UInt64AsStringJsonConverter : JsonConverter<ulong> {
    /// <inheritdoc/>
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ulong.Parse(s: (reader.GetString() ?? throw new JsonException(message: "expected a decimal string.")), style: NumberStyles.None, provider: CultureInfo.InvariantCulture);
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value: value.ToString(provider: CultureInfo.InvariantCulture));
}
