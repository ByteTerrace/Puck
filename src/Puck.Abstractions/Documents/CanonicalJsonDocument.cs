using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Puck.Abstractions.Documents;

/// <summary>The canonical write shape every document family in this repository shares: UTF-8 with no BOM, LF
/// newlines, two-space indentation, and exactly one trailing newline at EOF, so a load then save reproduces a file
/// byte-for-byte and every document stays diffable and git-friendly.</summary>
public static class CanonicalJsonDocument {
    // Relaxed escaping: these documents are read by this engine and by people, never embedded in HTML, so an
    // authored "a + b < c" or an em-dash is written as itself rather than as a \u escape.
    private static readonly JsonWriterOptions WriterOptions = new() {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true,
        NewLine = "\n",
    };

    /// <summary>Serializes a document to its canonical UTF-8 bytes.</summary>
    /// <param name="value">The document to serialize.</param>
    /// <param name="jsonTypeInfo">The document type's source-generated metadata.</param>
    /// <returns>The canonical UTF-8 byte form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static byte[] Serialize<T>(T value, JsonTypeInfo<T> jsonTypeInfo) where T : class {
        ArgumentNullException.ThrowIfNull(argument: value);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(
            options: WriterOptions,
            utf8Json: stream
        )) {
            JsonSerializer.Serialize(
                jsonTypeInfo: jsonTypeInfo,
                value: value,
                writer: writer
            );
        }

        stream.WriteByte(value: ((byte)'\n'));

        return stream.ToArray();
    }
    /// <summary>Serializes a JSON node (e.g. a hand-assembled basis delta) to its canonical UTF-8 bytes.</summary>
    /// <param name="node">The node to serialize.</param>
    /// <returns>The canonical UTF-8 byte form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
    public static byte[] Serialize(JsonNode node) {
        ArgumentNullException.ThrowIfNull(argument: node);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(
            options: WriterOptions,
            utf8Json: stream
        )) {
            node.WriteTo(writer: writer);
        }

        stream.WriteByte(value: ((byte)'\n'));

        return stream.ToArray();
    }
}
