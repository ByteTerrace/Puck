using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One thing a recipient was handed, at the boundary where it left the authority for that recipient: which
/// door handed it, the tick it was received at, its place in everything that recipient received, and its exact
/// bytes.</summary>
/// <param name="Principal">The recipient's label in the harness (<c>seat2</c>, <c>session:replica</c>).</param>
/// <param name="Sequence">The event's place among every event of the transcript; a recipient's own events keep this
/// order.</param>
/// <param name="Tick">The completed tick the recipient received it at, read from the transcript's clock when it
/// arrived.</param>
/// <param name="Door">The door that handed it: a console verb, a delivery kind, a query, an outcome, the edit echo or
/// the stream's lifetime. Each door has one encoding, so the door names how <paramref name="Payload"/> reads.</param>
/// <param name="Text">Whether <paramref name="Payload"/> is UTF-8 text rather than a binary record.</param>
/// <param name="Payload">The exact bytes received, with run-local nonces named by
/// <see cref="WorldDisclosureTranscript.Nonce(WorldDocumentVersion)"/>.</param>
public sealed record WorldDisclosureEvent(string Principal, long Sequence, ulong Tick, string Door, bool Text, byte[] Payload) {
    /// <summary>Returns whether a recipient could tell this event from <paramref name="other"/>: another door, tick,
    /// encoding or byte. The sequence and principal are where the event stands, not what was received.</summary>
    /// <param name="other">The other event.</param>
    /// <returns><see langword="true"/> when the two are received identically.</returns>
    public bool ReceivedAs(WorldDisclosureEvent other) {
        ArgumentNullException.ThrowIfNull(argument: other);
        return ((Tick == other.Tick) && (Text == other.Text) && string.Equals(a: Door, b: other.Door, comparisonType: StringComparison.Ordinal) &&
            Payload.AsSpan().SequenceEqual(other: other.Payload));
    }
}

/// <summary>
/// Everything the observed principals of one noninterference run received, as one ordered stream of raw events per
/// recipient: the input to <see cref="WorldDisclosureComparison"/>. Each event is recorded at the boundary where it
/// leaves the authority for its recipient (a session's sink, a submission's completion, a console line's answer, the
/// edit echo, a stream's attach and end) and stamped with the tick the transcript's clock reads as it arrives, so the
/// receipt tick is never reconstructed from the input that caused it. A later principal (a socket peer, a mounted
/// addon, a traveller) adds events of its own doors through the same <see cref="RecordText"/> and
/// <see cref="RecordBytes"/>, and the comparison takes them unchanged.
/// </summary>
/// <remarks>
/// <para>The one run-local nonce an event carries is <see cref="WorldDocumentVersion.Activation"/>, the id a server
/// mints at construction: <see cref="Nonce(WorldDocumentVersion)"/> replaces it with an id derived from the order it
/// first appeared, so two runs compare equal exactly when the same activation recurs at the same places. Every other
/// field is recorded as received, except the edit echo's transport counters, which the harness's own inputs set
/// (<see cref="RecordEcho"/>).</para>
/// <para>Beside the events, a run records its setup: what the variant's own steps and admissions did, and every
/// reason the run cannot judge (a refused or unanswered step, a session the world did not admit). A run with such a
/// reason is invalid, whatever its events say. Safe from any thread.</para>
/// </remarks>
/// <param name="clock">Reads the completed tick an arriving event is stamped with.</param>
public sealed class WorldDisclosureTranscript(Func<ulong> clock) {
    /// <summary>The transcript file's schema id.</summary>
    public const string SchemaId = "puck.disclosure.transcript.v2";
    /// <summary>The transcript's file name beside a run's other exports.</summary>
    public const string FileName = "disclosure.json";
    /// <summary>The payload size, in bytes, from which the file stores a payload once in its blob table.</summary>
    public const int BlobBytes = 512;

    private readonly Lock m_gate = new();
    private readonly List<WorldDisclosureEvent> m_events = [];
    private readonly List<string> m_setup = [];
    private readonly List<string> m_invalid = [];
    private readonly Dictionary<Guid, int> m_nonces = [];

    /// <summary>Gets a copy of every event, in the order they arrived.</summary>
    public IReadOnlyList<WorldDisclosureEvent> Events { get { lock (m_gate) { return [.. m_events]; } } }
    /// <summary>Gets a copy of the setup notes: what each variant step and admission did, in order.</summary>
    public IReadOnlyList<string> Setup { get { lock (m_gate) { return [.. m_setup]; } } }
    /// <summary>Gets a copy of the reasons the run cannot judge; empty for a valid run.</summary>
    public IReadOnlyList<string> Invalid { get { lock (m_gate) { return [.. m_invalid]; } } }

    /// <summary>Records text a recipient received.</summary>
    /// <param name="principal">The recipient's label.</param>
    /// <param name="door">The door that handed it.</param>
    /// <param name="text">The text, exactly as received.</param>
    public void RecordText(string principal, string door, string text) => Add(door: door, payload: Encoding.UTF8.GetBytes(s: text), principal: principal, text: true);
    /// <summary>Records a binary record a recipient received.</summary>
    /// <param name="principal">The recipient's label.</param>
    /// <param name="door">The door that handed it.</param>
    /// <param name="payload">The record's bytes, copied.</param>
    public void RecordBytes(string principal, string door, ReadOnlySpan<byte> payload) => Add(door: door, payload: payload.ToArray(), principal: principal, text: false);
    /// <summary>Records a console line's answer as the submitting principal received it: the line, how it ended
    /// (ran, refused or faulted) and the handler's output, line breaks intact.</summary>
    /// <param name="principal">The submitting principal's label.</param>
    /// <param name="line">The submitted line.</param>
    /// <param name="result">The handler's result.</param>
    public void RecordResponse(string principal, string line, in CommandResult result) {
        ArgumentNullException.ThrowIfNull(argument: line);
        var verb = line.AsSpan().Trim();
        var end = verb.IndexOfAny(values: " \t");

        RecordText(door: ((end < 0) ? verb : verb[..end]).ToString(), principal: principal,
            text: $"{line}\n{(result.Faulted ? "faulted" : (result.IsError ? "refused" : "ran"))}\n{result.Output}");
    }
    /// <summary>Records an edit echo a principal is narrated: the verdict of its own buffered act, its kind, whether it
    /// was refused or denied, and its message. The echo's connection and correlation ids are left out: the transport
    /// mints them for the submission before the world sees it, from a counter the harness's in-process principals
    /// share with the variant's own operator steps, so they count the run's inputs and carry nothing of its state.</summary>
    /// <param name="principal">The acting principal's label.</param>
    /// <param name="echo">The echo.</param>
    public void RecordEcho(string principal, in WorldEditEcho echo) => RecordText(door: "echo", principal: principal,
        text: $"kind={echo.Kind} rejected={echo.Rejected} denied={echo.Denied}\n{echo.Message}");
    /// <summary>Notes what a setup step did, for the report; never compared.</summary>
    /// <param name="note">The note.</param>
    public void NoteSetup(string note) { lock (m_gate) { m_setup.Add(item: note); } }
    /// <summary>Marks the run unable to judge, naming why.</summary>
    /// <param name="reason">The reason.</param>
    public void Invalidate(string reason) { lock (m_gate) { m_invalid.Add(item: reason); } }
    /// <summary>Names a document version's run-local activation by the order it first appeared in this transcript.</summary>
    /// <param name="version">The version received.</param>
    /// <returns>The version with its activation replaced by a deterministic id for that order (never
    /// <see cref="Guid.Empty"/> for a delivered version), its sequence unchanged.</returns>
    public WorldDocumentVersion Nonce(WorldDocumentVersion version) {
        if (!version.IsDelivered) { return version; }
        lock (m_gate) {
            if (!m_nonces.TryGetValue(key: version.Activation, value: out var ordinal)) {
                ordinal = m_nonces.Count;
                m_nonces[version.Activation] = ordinal;
            }
            return version with { Activation = new Guid(a: (ordinal + 1), b: 0, c: 0, d: 0, e: 0, f: 0, g: 0, h: 0, i: 0, j: 0, k: 0) };
        }
    }
    /// <summary>Spells a document version with its activation named by order (<c>#0.3</c>).</summary>
    /// <param name="version">The version received.</param>
    /// <returns>The spelling.</returns>
    public string Spell(WorldDocumentVersion version) => (Nonce(version: version) is { IsDelivered: true } named
        ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"#{(BitConverter.ToInt32(value: named.Activation.ToByteArray(), startIndex: 0) - 1)}.{named.Sequence}")
        : string.Create(provider: CultureInfo.InvariantCulture, handler: $"undelivered.{version.Sequence}"));

    /// <summary>Writes the transcript as JSON: each event inline, or by reference into the blob table from
    /// <see cref="BlobBytes"/>.</summary>
    /// <returns>The UTF-8 document.</returns>
    public byte[] Serialize() {
        using var stream = new MemoryStream();
        var blobs = new SortedDictionary<string, WorldDisclosureEvent>(comparer: StringComparer.Ordinal);

        using (var writer = new Utf8JsonWriter(utf8Json: stream, options: new JsonWriterOptions { Indented = true })) {
            writer.WriteStartObject();
            writer.WriteString(propertyName: "schema", value: SchemaId);
            WriteStrings(name: "invalid", values: Invalid, writer: writer);
            WriteStrings(name: "setup", values: Setup, writer: writer);
            writer.WriteStartArray(propertyName: "events");
            foreach (var entry in Events) {
                writer.WriteStartObject();
                writer.WriteNumber(propertyName: "seq", value: entry.Sequence);
                writer.WriteString(propertyName: "principal", value: entry.Principal);
                writer.WriteNumber(propertyName: "tick", value: entry.Tick);
                writer.WriteString(propertyName: "door", value: entry.Door);
                if (entry.Payload.Length >= BlobBytes) {
                    var reference = string.Create(provider: CultureInfo.InvariantCulture,
                        handler: $"{Convert.ToHexStringLower(bytes: SHA256.HashData(source: entry.Payload).AsSpan(start: 0, length: 8))}-{entry.Payload.Length}");

                    blobs.TryAdd(key: reference, value: entry);
                    writer.WriteString(propertyName: (entry.Text ? "textBlob" : "bytesBlob"), value: reference);
                } else {
                    WritePayload(entry: entry, name: (entry.Text ? "text" : "bytes"), writer: writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject(propertyName: "blobs");
            foreach (var (reference, entry) in blobs) { WritePayload(entry: entry, name: reference, writer: writer); }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
    /// <summary>Reads a transcript <see cref="Serialize"/> wrote.</summary>
    /// <param name="json">The UTF-8 document.</param>
    /// <returns>The transcript; its clock reads zero.</returns>
    /// <exception cref="InvalidDataException">The document is not a disclosure transcript of this schema.</exception>
    public static WorldDisclosureTranscript Parse(ReadOnlySpan<byte> json) {
        var reader = new Utf8JsonReader(jsonData: json);
        using var document = JsonDocument.ParseValue(reader: ref reader);
        var root = document.RootElement;

        if (!root.TryGetProperty(propertyName: "schema", value: out var schema) || (schema.GetString() != SchemaId)) {
            throw new InvalidDataException(message: $"not a {SchemaId} document");
        }
        var transcript = new WorldDisclosureTranscript(clock: static () => 0UL);
        var blobs = root.GetProperty(propertyName: "blobs");

        foreach (var reason in root.GetProperty(propertyName: "invalid").EnumerateArray()) { transcript.Invalidate(reason: (reason.GetString() ?? string.Empty)); }
        foreach (var note in root.GetProperty(propertyName: "setup").EnumerateArray()) { transcript.NoteSetup(note: (note.GetString() ?? string.Empty)); }
        foreach (var entry in root.GetProperty(propertyName: "events").EnumerateArray()) {
            var (text, payload) = (entry.TryGetProperty(propertyName: "text", value: out var inline) ? (true, inline.GetString())
                : entry.TryGetProperty(propertyName: "bytes", value: out inline) ? (false, inline.GetString())
                : entry.TryGetProperty(propertyName: "textBlob", value: out inline) ? (true, blobs.GetProperty(propertyName: (inline.GetString() ?? string.Empty)).GetString())
                : (false, blobs.GetProperty(propertyName: (entry.GetProperty(propertyName: "bytesBlob").GetString() ?? string.Empty)).GetString()));

            lock (transcript.m_gate) {
                transcript.m_events.Add(item: new WorldDisclosureEvent(Door: (entry.GetProperty(propertyName: "door").GetString() ?? string.Empty),
                    Payload: (text ? Encoding.UTF8.GetBytes(s: (payload ?? string.Empty)) : Convert.FromBase64String(s: (payload ?? string.Empty))),
                    Principal: (entry.GetProperty(propertyName: "principal").GetString() ?? string.Empty), Sequence: entry.GetProperty(propertyName: "seq").GetInt64(),
                    Text: text, Tick: entry.GetProperty(propertyName: "tick").GetUInt64()));
            }
        }
        return transcript;
    }

    private void Add(string principal, string door, byte[] payload, bool text) {
        ArgumentException.ThrowIfNullOrEmpty(argument: principal);
        ArgumentException.ThrowIfNullOrEmpty(argument: door);
        var tick = clock();

        lock (m_gate) {
            m_events.Add(item: new WorldDisclosureEvent(Door: door, Payload: payload, Principal: principal, Sequence: m_events.Count, Text: text, Tick: tick));
        }
    }
    private static void WritePayload(Utf8JsonWriter writer, string name, WorldDisclosureEvent entry) {
        if (entry.Text) {
            writer.WriteString(propertyName: name, value: Encoding.UTF8.GetString(bytes: entry.Payload));
        } else {
            writer.WriteBase64String(propertyName: name, bytes: entry.Payload);
        }
    }
    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values) {
        writer.WriteStartArray(propertyName: name);
        foreach (var value in values) { writer.WriteStringValue(value: value); }
        writer.WriteEndArray();
    }
}
