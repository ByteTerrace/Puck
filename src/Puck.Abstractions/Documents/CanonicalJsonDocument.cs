using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Documents;

/// <summary>The canonical write shape every document family in this repository shares: UTF-8 with no BOM, LF
/// newlines, two-space indentation, and exactly one trailing newline at EOF, so a load then save reproduces a file
/// byte-for-byte and every document stays diffable and git-friendly. <see cref="SerializeCompact{T}"/> writes the
/// same document with no whitespace at all, for a product a machine stores rather than a person reads.</summary>
public static class CanonicalJsonDocument {
    // Relaxed escaping: these documents are read by this engine and by people, never embedded in HTML, so an
    // authored "a + b < c" or an em-dash is written as itself rather than as a \u escape.
    private static readonly JsonWriterOptions WriterOptions = new() {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true,
        NewLine = "\n",
    };
    private static readonly JsonWriterOptions CompactWriterOptions = new() {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Parses the root object of a UTF-8 document with unique member names.</summary>
    /// <exception cref="JsonException">The bytes do not contain a JSON object with unique member names.</exception>
    public static JsonObject ParseObject(ReadOnlySpan<byte> bytes) =>
        JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject ??
        throw new JsonException("Expected a JSON object.");

    /// <summary>Serializes a document to its compact canonical UTF-8 bytes: the members, order, and escaping of
    /// <see cref="Serialize{T}(T, JsonTypeInfo{T})"/>, with no indentation, newlines, or trailing newline.</summary>
    /// <param name="value">The document to serialize.</param>
    /// <param name="jsonTypeInfo">The document type's source-generated metadata.</param>
    /// <returns>The compact canonical UTF-8 byte form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static byte[] SerializeCompact<T>(T value, JsonTypeInfo<T> jsonTypeInfo) where T : class {
        ArgumentNullException.ThrowIfNull(argument: value);

        return SerializeCompact(value, (JsonTypeInfo)jsonTypeInfo);
    }

    /// <summary>Serializes a canonical member using its declared type, including a null member when present.</summary>
    /// <param name="value">The member's value.</param>
    /// <param name="jsonTypeInfo">The member type's source-generated metadata.</param>
    /// <returns>The compact canonical UTF-8 bytes.</returns>
    public static byte[] SerializeCompact(object? value, JsonTypeInfo jsonTypeInfo) {
        using var output = new OwnedBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(
            options: CompactWriterOptions,
            bufferWriter: output
        )) {
            JsonSerializer.Serialize(
                jsonTypeInfo: jsonTypeInfo,
                value: value,
                writer: writer
            );
        }

        return output.ToArray();
    }
    /// <summary>Serializes a document to its canonical UTF-8 bytes.</summary>
    /// <param name="value">The document to serialize.</param>
    /// <param name="jsonTypeInfo">The document type's source-generated metadata.</param>
    /// <returns>The canonical UTF-8 byte form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static byte[] Serialize<T>(T value, JsonTypeInfo<T> jsonTypeInfo) where T : class {
        ArgumentNullException.ThrowIfNull(argument: value);

        using var output = new OwnedBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(
            options: WriterOptions,
            bufferWriter: output
        )) {
            JsonSerializer.Serialize(
                jsonTypeInfo: jsonTypeInfo,
                value: value,
                writer: writer
            );
        }

        output.Write(value: "\n"u8);

        return output.ToArray();
    }
    /// <summary>Serializes a JSON node to its compact canonical UTF-8 bytes: the members, order, and escaping of
    /// <see cref="Serialize(JsonNode)"/>, with no indentation, newlines, or trailing newline.</summary>
    /// <param name="node">The node to serialize.</param>
    /// <returns>The compact canonical UTF-8 byte form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
    public static byte[] SerializeCompact(JsonNode node) {
        ArgumentNullException.ThrowIfNull(argument: node);

        using var output = new OwnedBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(
            options: CompactWriterOptions,
            bufferWriter: output
        )) {
            node.WriteTo(writer: writer);
        }

        return output.ToArray();
    }
    /// <summary>Serializes a JSON node (e.g. a hand-assembled basis delta) to its canonical UTF-8 bytes.</summary>
    /// <param name="node">The node to serialize.</param>
    /// <returns>The canonical UTF-8 byte form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <see langword="null"/>.</exception>
    public static byte[] Serialize(JsonNode node) {
        ArgumentNullException.ThrowIfNull(argument: node);

        using var output = new OwnedBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(
            options: WriterOptions,
            bufferWriter: output
        )) {
            node.WriteTo(writer: writer);
        }

        output.Write(value: "\n"u8);

        return output.ToArray();
    }
}
