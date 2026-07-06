using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Assets;

namespace Puck.Demo.Forge;

/// <summary>One row of a pattern or effect: a note name (<c>"C5"</c>, <c>"G#4"</c> — scientific pitch notation,
/// octaves 3..6), <c>"---"</c> to hold/rest (silence with no new trigger — the previous note's envelope keeps
/// decaying), or <c>"OFF"</c> to cut the voice immediately (envelope zeroed, DAC off). Optional per-row duty and
/// envelope let a pattern re-voice mid-song; both normalize to defaults at load.</summary>
/// <param name="Note">The row's note name, <c>"---"</c>, or <c>"OFF"</c>.</param>
/// <param name="Duty">The pulse duty cycle 0..3 (0 = 12.5%, 1 = 25%, 2 = 50%, 3 = 75%; null = the pattern/effect
/// default). Ignored on the noise voice.</param>
/// <param name="Envelope">The raw NRx2 envelope byte (null = the pattern/effect default).</param>
public sealed record AudioRowDocument(string Note, int? Duty, int? Envelope) {
    /// <summary>The hold/rest row text: keep playing the previous step's note (or silence) without retriggering.</summary>
    public const string Hold = "---";
    /// <summary>The cut row text: silence the voice immediately (envelope zeroed, DAC off).</summary>
    public const string Off = "OFF";
}

/// <summary>One named effect: its row sequence and which voice it plays on (<c>"pulse1"</c> or <c>"noise"</c> — the
/// document never targets pulse 2, the music voice, so effects can never fight the loop for a channel).</summary>
/// <param name="Voice">The hardware voice name (null = <c>"pulse1"</c>).</param>
/// <param name="Rows">The row sequence.</param>
public sealed record AudioEffectDocument(string? Voice, IReadOnlyList<AudioRowDocument> Rows) {
    /// <summary>The pulse-1 voice name.</summary>
    public const string VoicePulse1 = "pulse1";
    /// <summary>The noise voice name.</summary>
    public const string VoiceNoise = "noise";
}

/// <summary>
/// The <c>puck.audio.v1</c> document — authored music as DATA: a short song compiles to the exact ROM sound-table
/// streams <see cref="Framework.ApuSoundDriver"/> already plays (the pulse-2 music loop, plus named pulse-1/noise
/// effect streams), through <see cref="AudioDocumentCompiler"/>. Document doctrine applies throughout: every OPTIONAL
/// member is nullable, validated only when present, and normalized at load (<see cref="AudioDocumentStore"/>) — an
/// omitted member never becomes a null the compiler has to reason about. No wall-clock, RNG, or float anywhere in
/// the schema or the compile path: <see cref="Tempo"/> is an integer frame count, and every row resolves through
/// integer millihertz note-period math (<see cref="Framework.ApuNotePeriod"/>).
/// </summary>
/// <param name="Schema">The document version tag (<c>puck.audio.v1</c>).</param>
/// <param name="Name">The song's display name (shown on the jukebox title screen; null = "UNTITLED").</param>
/// <param name="Tempo">Frames per pattern row (null = 8 — the framework's stock eighth-note rate at 60 fps).</param>
/// <param name="Patterns">The song's patterns, each a list of rows (null/empty = one silent 16-row pattern).</param>
/// <param name="Order">The pattern-index play order for the loop (null = every declared pattern, in order).</param>
/// <param name="Effects">Named effect streams, keyed by name (null = none).</param>
public sealed record AudioDocument(
    string? Schema,
    string? Name,
    int? Tempo,
    IReadOnlyList<IReadOnlyList<AudioRowDocument>>? Patterns,
    IReadOnlyList<int>? Order,
    IReadOnlyDictionary<string, AudioEffectDocument>? Effects
) {
    /// <summary>The version tag every saved document carries.</summary>
    public const string CurrentSchema = "puck.audio.v1";
    /// <summary>The default tempo (frames per row) when the document omits one.</summary>
    public const int DefaultTempo = 8;
    /// <summary>The default row count of the fallback silent pattern.</summary>
    public const int DefaultPatternRowCount = 16;
}

/// <summary>
/// Loads and saves <see cref="AudioDocument"/>s as indented camel-case JSON — the same
/// <c>IncludeFields</c>-free, enum-string, case-insensitive style as <see cref="Creator.CreationStore"/> (this
/// document carries no <c>Vector3</c>/<c>Quaternion</c> fields, so <c>IncludeFields</c> is not load-bearing here, but
/// the shared options instance keeps every document family serializing identically).
/// </summary>
public static class AudioDocumentStore {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>The folder tracker documents persist under (relative to the working directory, the audio-document
    /// sibling of <see cref="Creator.CreationStore.Folder"/>).</summary>
    public static string Folder => "tunes";

    /// <summary>Serializes a document to indented camel-case JSON.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The JSON text.</returns>
    public static string ToJson(AudioDocument document) =>
        JsonSerializer.Serialize(options: JsonOptions, value: document);

    /// <summary>Builds a fresh blank document (one silent pattern, default tempo) through the SAME normalize path
    /// every loaded document takes — never a hand-built record — so a brand-new working document in the tracker can
    /// never drift from what a round-tripped save/load would produce. No file I/O.</summary>
    /// <param name="name">The new document's display name (null = "UNTITLED").</param>
    /// <returns>The normalized blank document.</returns>
    public static AudioDocument Blank(string? name = null) =>
        Normalize(document: new AudioDocument(Effects: null, Name: name, Order: null, Patterns: null, Schema: null, Tempo: null));

    /// <summary>Saves a document to a file path, and, when <paramref name="store"/> is given, also lands the
    /// canonical bytes in the content-addressed store under <c>SetRef("tunes", name)</c>.</summary>
    /// <param name="document">The document to save.</param>
    /// <param name="path">The destination path.</param>
    /// <param name="store">The content-addressed store to also write into (null = today's file-only behavior).</param>
    /// <param name="refName">The store ref name (only meaningful when <paramref name="store"/> is given; null = the
    /// file's name without extension).</param>
    public static void Save(AudioDocument document, string path, ContentAddressedStore? store = null, string? refName = null) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(path: Path.GetFullPath(path: path));

        if (!string.IsNullOrEmpty(value: directory)) {
            _ = Directory.CreateDirectory(path: directory);
        }

        var json = ToJson(document: (document with { Schema = AudioDocument.CurrentSchema }));

        File.WriteAllText(contents: json, path: path);

        if (store is not null) {
            var hash = store.Put(content: System.Text.Encoding.UTF8.GetBytes(s: json));

            store.SetRef(category: "tunes", name: (refName ?? Path.GetFileNameWithoutExtension(path: path)), hash: hash);
        }
    }

    /// <summary>Saves a document under <c>./tunes/&lt;name&gt;.audio.json</c> (the name is sanitized to letters,
    /// digits, dashes, and underscores) — the tracker's save verb/button, mirroring
    /// <see cref="Creator.CreationStore.Save(Creator.CreationDocument, string)"/>'s convention exactly.</summary>
    /// <param name="document">The document to save.</param>
    /// <param name="name">The save handle.</param>
    /// <param name="store">The content-addressed store to also write into (null = today's file-only behavior).</param>
    /// <returns>The written path.</returns>
    public static string SaveNamed(AudioDocument document, string name, ContentAddressedStore? store = null) {
        ArgumentNullException.ThrowIfNull(document);

        var path = PathFor(name: name);

        Save(document: document, path: path, store: store, refName: SanitizeName(name: name));

        return path;
    }

    /// <summary>Loads a document by save handle OR file path (a handle resolves under <c>./tunes/</c>).</summary>
    /// <param name="nameOrPath">The save handle or an explicit file path.</param>
    /// <returns>The normalized document, or null when nothing readable exists at the location.</returns>
    public static AudioDocument? LoadNamed(string nameOrPath) {
        ArgumentException.ThrowIfNullOrEmpty(nameOrPath);

        var path = (File.Exists(path: nameOrPath) ? nameOrPath : PathFor(name: nameOrPath));

        return (File.Exists(path: path) ? Load(path: path) : null);
    }

    /// <summary>Lists the save handles under <c>./tunes/</c>.</summary>
    /// <returns>The handles, sorted ordinally.</returns>
    public static IReadOnlyList<string> List() {
        if (!Directory.Exists(path: Folder)) {
            return [];
        }

        var names = new List<string>();

        foreach (var path in Directory.EnumerateFiles(path: Folder, searchPattern: "*.audio.json")) {
            names.Add(item: Path.GetFileName(path: path)[..^".audio.json".Length]);
        }

        names.Sort(comparer: StringComparer.Ordinal);

        return names;
    }

    private static string PathFor(string name) =>
        Path.Combine(path1: Folder, path2: $"{SanitizeName(name: name)}.audio.json");

    // Mirrors CreationStore's Sanitize exactly (non [A-Za-z0-9_-] chars -> '-'; empty -> a safe default).
    private static string SanitizeName(string name) {
        var builder = new System.Text.StringBuilder(capacity: name.Length);

        foreach (var character in name) {
            _ = builder.Append(value: (char.IsAsciiLetterOrDigit(c: character) || (character is '-' or '_')) ? character : '-');
        }

        return ((builder.Length > 0) ? builder.ToString() : "tune");
    }

    /// <summary>Loads a document from a file path. The result is normalized — never trust persisted derived values.
    /// Rejects an unrecognized <c>schema</c> tag with a clear error (a version bump orphans old files loudly, never
    /// silently).</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The normalized document.</returns>
    public static AudioDocument Load(string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path: path)) {
            throw new FileNotFoundException(message: $"No audio document at '{path}'.", fileName: path);
        }

        var json = File.ReadAllText(path: path);
        var document = (JsonSerializer.Deserialize<AudioDocument>(json: json, options: JsonOptions)
            ?? throw new InvalidDataException(message: $"'{path}' deserialized to null."));

        if ((document.Schema is { Length: > 0 } schema) && !string.Equals(a: schema, b: AudioDocument.CurrentSchema, comparisonType: StringComparison.Ordinal)) {
            throw new InvalidDataException(message: $"'{path}' declares schema '{schema}', not the recognized '{AudioDocument.CurrentSchema}'.");
        }

        return Normalize(document: document);
    }

    // Normalization is the load-time half of the document doctrine: clamp/default every optional member so the
    // compiler never sees a null it has to reason about.
    private static AudioDocument Normalize(AudioDocument document) {
        var patterns = new List<IReadOnlyList<AudioRowDocument>>(capacity: (document.Patterns?.Count ?? 1));

        foreach (var pattern in (document.Patterns ?? [])) {
            patterns.Add(item: NormalizeRows(rows: pattern));
        }

        if (patterns.Count == 0) {
            patterns.Add(item: NormalizeRows(rows: null));
        }

        var order = new List<int>(capacity: (document.Order?.Count ?? patterns.Count));

        foreach (var index in (document.Order ?? Enumerable.Range(start: 0, count: patterns.Count))) {
            if ((index < 0) || (index >= patterns.Count)) {
                throw new ArgumentOutOfRangeException(paramName: nameof(document), message: $"The play order references pattern {index}, but only {patterns.Count} pattern(s) are declared.");
            }

            order.Add(item: index);
        }

        if (order.Count == 0) {
            order.Add(item: 0);
        }

        var effects = new Dictionary<string, AudioEffectDocument>(comparer: StringComparer.Ordinal);

        foreach (var (name, effect) in (document.Effects ?? new Dictionary<string, AudioEffectDocument>())) {
            effects[name] = (effect with {
                Rows = NormalizeRows(rows: effect.Rows),
                Voice = (string.Equals(a: effect.Voice, b: AudioEffectDocument.VoiceNoise, comparisonType: StringComparison.OrdinalIgnoreCase)
                    ? AudioEffectDocument.VoiceNoise
                    : AudioEffectDocument.VoicePulse1),
            });
        }

        return (document with {
            Effects = effects,
            Name = (string.IsNullOrWhiteSpace(value: document.Name) ? "UNTITLED" : document.Name.Trim()),
            Order = order,
            Patterns = patterns,
            Schema = AudioDocument.CurrentSchema,
            Tempo = Math.Max(val1: (document.Tempo ?? AudioDocument.DefaultTempo), val2: 1),
        });
    }

    private static List<AudioRowDocument> NormalizeRows(IReadOnlyList<AudioRowDocument>? rows) {
        var normalized = new List<AudioRowDocument>(capacity: (rows?.Count ?? AudioDocument.DefaultPatternRowCount));

        foreach (var row in (rows ?? [])) {
            if (string.IsNullOrWhiteSpace(value: row.Note)) {
                throw new ArgumentException(message: "A row's note may not be empty (use \"---\" to hold/rest).", paramName: nameof(rows));
            }

            normalized.Add(item: row with {
                Duty = Math.Clamp(value: (row.Duty ?? 2), max: 3, min: 0),
                Note = row.Note.Trim().ToUpperInvariant(),
            });
        }

        if (normalized.Count == 0) {
            for (var index = 0; (index < AudioDocument.DefaultPatternRowCount); index++) {
                normalized.Add(item: new AudioRowDocument(Duty: 2, Envelope: null, Note: AudioRowDocument.Hold));
            }
        }

        return normalized;
    }
}
