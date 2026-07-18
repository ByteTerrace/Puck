using System.Text.Json;

namespace Puck.Recording.Document;

/// <summary>
/// The canonical (de)serializer for the recording document. <see cref="Serialize"/> emits a stable canonical form
/// — member order follows the record declaration, invariant number formatting, LF newlines, two-space indentation,
/// no BOM, and exactly one trailing newline — so a load then save reproduces the file byte-for-byte and documents
/// stay diffable. Loading runs the one thick <see cref="RecordingDocumentValidator"/> and never half-accepts.
/// </summary>
public static class RecordingDocumentSerialization {
    private static readonly JsonWriterOptions s_writerOptions = new() {
        Indented = true,
        NewLine = "\n",
    };

    /// <summary>Serializes a document to its canonical UTF-8 bytes.</summary>
    /// <param name="document">The document to serialize.</param>
    /// <returns>The canonical UTF-8 byte form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static byte[] Serialize(RecordingDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(utf8Json: stream, options: s_writerOptions)) {
            JsonSerializer.Serialize(writer: writer, value: document, jsonTypeInfo: RecordingJsonContext.Default.RecordingDocument);
        }

        stream.WriteByte(value: (byte)'\n');

        return stream.ToArray();
    }

    /// <summary>Writes a document to <paramref name="path"/> in canonical form.</summary>
    /// <param name="document">The document to write.</param>
    /// <param name="path">The destination file path.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    public static long Save(RecordingDocument document, string path) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentException.ThrowIfNullOrEmpty(argument: path);

        var bytes = Serialize(document: document);

        File.WriteAllBytes(path: path, bytes: bytes);

        return bytes.LongLength;
    }

    /// <summary>Parses and validates a document from UTF-8 JSON.</summary>
    /// <param name="utf8Json">The document bytes.</param>
    /// <param name="document">The parsed, validated document on success; otherwise <see langword="null"/>.</param>
    /// <param name="reason">The one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the document parses and validates.</returns>
    public static bool TryLoad(ReadOnlySpan<byte> utf8Json, out RecordingDocument? document, out string reason) {
        RecordingDocument? parsed;

        try {
            parsed = JsonSerializer.Deserialize(utf8Json: utf8Json, jsonTypeInfo: RecordingJsonContext.Default.RecordingDocument);
        } catch (JsonException exception) {
            document = null;
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }

        if (parsed is null) {
            document = null;
            reason = "the document was null.";

            return false;
        }

        if (!RecordingDocumentValidator.TryValidate(document: parsed, reason: out reason)) {
            document = null;

            return false;
        }

        document = parsed;

        return true;
    }
}
