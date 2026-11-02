using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Puck.World.Server;

/// <summary>One thing a recipient was handed: by whom it was answered (the door), when, and its exact bytes.</summary>
/// <param name="Principal">The recipient's label in the harness (<c>seat2</c>, <c>session:replica</c>).</param>
/// <param name="Door">The door that answered: a console verb, a delivery kind, a query or an outcome.</param>
/// <param name="Tick">The completed tick the recipient received it at.</param>
/// <param name="Bytes">The canonical text of what it received. A large payload stands as a <c>blob:</c> reference into
/// <see cref="WorldDisclosureTranscript.Blobs"/>, so equal payloads are stored once.</param>
public sealed record WorldDisclosureEntry(string Principal, string Door, ulong Tick, string Bytes);

/// <summary>
/// Everything the observed principals of one noninterference run received, in order: the input to
/// <see cref="WorldDisclosureComparison"/>. The noninterference harness (<c>puck disclosure</c>) records one
/// transcript per world of a pair and compares them byte for byte.
/// </summary>
/// <remarks>Per-run nonces (an authority's activation id) are not hidden state: <see cref="Nonce"/> names each by the
/// order it first appeared, so two runs compare equal exactly when the same nonce recurs at the same places. Safe from
/// any thread.</remarks>
public sealed class WorldDisclosureTranscript {
    /// <summary>The transcript file's schema id.</summary>
    public const string SchemaId = "puck.disclosure.transcript.v1";
    /// <summary>The prefix of a payload stored once in <see cref="Blobs"/>.</summary>
    public const string BlobPrefix = "blob:";
    /// <summary>The transcript's file name beside a run's other exports.</summary>
    public const string FileName = "disclosure.json";

    private readonly Lock m_gate = new();
    private readonly List<WorldDisclosureEntry> m_entries = [];
    private readonly Dictionary<string, string> m_blobs = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<Guid, int> m_nonces = [];

    /// <summary>Gets a copy of every entry, in the order they were recorded.</summary>
    public IReadOnlyList<WorldDisclosureEntry> Entries { get { lock (m_gate) { return [.. m_entries]; } } }
    /// <summary>Gets a copy of the stored payloads by reference.</summary>
    public IReadOnlyDictionary<string, string> Blobs { get { lock (m_gate) { return new Dictionary<string, string>(dictionary: m_blobs, comparer: StringComparer.Ordinal); } } }

    /// <summary>Records one thing a principal received.</summary>
    /// <param name="principal">The recipient's label.</param>
    /// <param name="door">The door that answered.</param>
    /// <param name="tick">The completed tick.</param>
    /// <param name="bytes">The canonical text received.</param>
    public void Record(string principal, string door, ulong tick, string bytes) {
        lock (m_gate) { m_entries.Add(item: new WorldDisclosureEntry(Bytes: bytes, Door: door, Principal: principal, Tick: tick)); }
    }
    /// <summary>Stores a large payload once and returns the reference an entry carries for it: its content hash and
    /// length, so a reader can tell two payloads apart by the reference alone.</summary>
    /// <param name="content">The payload's canonical text (JSON, for a document).</param>
    /// <returns>The <c>blob:&lt;hash&gt; bytes=&lt;n&gt;</c> reference.</returns>
    public string Blob(string content) {
        ArgumentNullException.ThrowIfNull(argument: content);
        var bytes = Encoding.UTF8.GetBytes(s: content);
        var reference = string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"{BlobPrefix}{Convert.ToHexStringLower(bytes: SHA256.HashData(source: bytes).AsSpan(start: 0, length: 8))} bytes={bytes.Length}");

        lock (m_gate) { m_blobs.TryAdd(key: reference, value: content); }
        return reference;
    }
    /// <summary>Names a per-run nonce by the order it first appeared in this transcript (<c>#0</c>, <c>#1</c>, …).</summary>
    /// <param name="value">The nonce.</param>
    /// <returns>Its ordinal name.</returns>
    public string Nonce(Guid value) {
        lock (m_gate) {
            if (!m_nonces.TryGetValue(key: value, value: out var ordinal)) {
                ordinal = m_nonces.Count;
                m_nonces[value] = ordinal;
            }
            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"#{ordinal}");
        }
    }
    /// <summary>Writes the transcript as JSON.</summary>
    /// <returns>The UTF-8 document.</returns>
    public byte[] Serialize() {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(utf8Json: stream, options: new JsonWriterOptions { Indented = true })) {
            writer.WriteStartObject();
            writer.WriteString(propertyName: "schema", value: SchemaId);
            writer.WriteStartArray(propertyName: "entries");
            foreach (var entry in Entries) {
                writer.WriteStartObject();
                writer.WriteString(propertyName: "principal", value: entry.Principal);
                writer.WriteString(propertyName: "door", value: entry.Door);
                writer.WriteNumber(propertyName: "tick", value: entry.Tick);
                writer.WriteString(propertyName: "bytes", value: entry.Bytes);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject(propertyName: "blobs");
            foreach (var (reference, content) in Blobs.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
                writer.WriteString(propertyName: reference, value: content);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
    /// <summary>Reads a transcript <see cref="Serialize"/> wrote.</summary>
    /// <param name="json">The UTF-8 document.</param>
    /// <returns>The transcript.</returns>
    /// <exception cref="InvalidDataException">The document is not a disclosure transcript.</exception>
    public static WorldDisclosureTranscript Parse(ReadOnlySpan<byte> json) {
        var reader = new Utf8JsonReader(jsonData: json);
        using var document = JsonDocument.ParseValue(reader: ref reader);
        var root = document.RootElement;

        if (!root.TryGetProperty(propertyName: "schema", value: out var schema) || (schema.GetString() != SchemaId)) {
            throw new InvalidDataException(message: $"not a {SchemaId} document");
        }
        var transcript = new WorldDisclosureTranscript();

        foreach (var entry in root.GetProperty(propertyName: "entries").EnumerateArray()) {
            transcript.Record(bytes: entry.GetProperty(propertyName: "bytes").GetString() ?? string.Empty,
                door: entry.GetProperty(propertyName: "door").GetString() ?? string.Empty,
                principal: entry.GetProperty(propertyName: "principal").GetString() ?? string.Empty,
                tick: entry.GetProperty(propertyName: "tick").GetUInt64());
        }
        foreach (var blob in root.GetProperty(propertyName: "blobs").EnumerateObject()) {
            transcript.m_blobs[blob.Name] = (blob.Value.GetString() ?? string.Empty);
        }
        return transcript;
    }
}
