using System.Text.Json;

namespace Puck.Authoring;

/// <summary>One row of a pattern or effect: a note name (<c>"C5"</c>, <c>"G#4"</c> — scientific pitch notation,
/// octaves 3..6), <c>"---"</c> to hold/rest (silence with no new trigger — the previous note's envelope keeps
/// decaying), or <c>"OFF"</c> to cut the voice immediately (envelope zeroed, DAC off). Optional per-row duty and
/// envelope let a pattern re-voice mid-song; both normalize to defaults through <see cref="AudioCanonicalizer.Normalize"/>.</summary>
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
/// The <c>puck.audio.v1</c> document — authored music as DATA: a short song describing the exact ROM sound-table
/// streams the SM83 game framework's sound driver plays (the pulse-2 music loop, plus named pulse-1/noise effect
/// streams), compiled by <c>Puck.Forge</c>'s <c>AudioDocumentCompiler</c>. Document doctrine applies throughout:
/// every OPTIONAL member is nullable, validated only when present, and normalized through
/// <see cref="AudioCanonicalizer"/> — an omitted member never becomes a null a compiler has to reason about. No
/// wall-clock, RNG, or float anywhere in the schema or the compile path: <see cref="Tempo"/> is an integer frame
/// count, and every row resolves through integer millihertz note-period math.
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

    /// <summary>Unknown members preserved across a round-trip — the data-side plugin extensibility posture. Null
    /// when the document carries no unknown members. A settable (not <c>init</c>) accessor is required:
    /// System.Text.Json appends to it during deserialization.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>
/// THE strict validate → normalize → canonicalize boundary every <see cref="AudioDocument"/> crosses before it is
/// trusted, persisted, or embedded — the audio family's adapter over <see cref="DocumentCanonicalizer"/>: an absent
/// or foreign schema rejects loudly (never a silent relabel), every violation is collected in one pass, and
/// canonical bytes + hash always come from the same result. <see cref="AudioDocumentStore"/> rides this exclusively.
/// </summary>
public static class AudioCanonicalizer {
    private static readonly HashSet<string> KnownMemberNames = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "schema", "name", "tempo", "patterns", "order", "effects",
    };

    /// <summary>Validates a document's schema and structural invariants in one pass — every violation is collected
    /// rather than throwing on the first. An absent or foreign <see cref="AudioDocument.Schema"/> short-circuits to
    /// that one violation, since no other check has a defined meaning against an unrecognized document shape. Only
    /// invariants normalization cannot repair without silently discarding meaning (an empty note text, a play-order
    /// entry naming a pattern that is not there) are failures; clampable values (duty, tempo) are normalization's
    /// job.</summary>
    /// <param name="document">The document to validate, as deserialized — not yet normalized.</param>
    /// <returns>Every violation found; empty when the document is a valid <c>puck.audio.v1</c> value.</returns>
    public static IReadOnlyList<DocumentValidationError> Validate(AudioDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (DocumentCanonicalizer.SchemaViolationMessage(declared: document.Schema, recognized: AudioDocument.CurrentSchema) is { } schemaViolation) {
            return [new DocumentValidationError(Path: "schema", Message: schemaViolation)];
        }

        var errors = new List<DocumentValidationError>();

        for (var i = 0; i < (document.Patterns?.Count ?? 0); i++) {
            ValidateRows(errors: errors, path: $"patterns[{i}]", rows: document.Patterns![i]);
        }

        // The play order resolves against the pattern list normalization will materialize: the declared patterns, or
        // the one fallback silent pattern when none are declared.
        var effectivePatternCount = Math.Max(val1: (document.Patterns?.Count ?? 0), val2: 1);

        for (var i = 0; i < (document.Order?.Count ?? 0); i++) {
            var index = document.Order![i];

            if ((index < 0) || (index >= effectivePatternCount)) {
                errors.Add(item: new(Path: $"order[{i}]", Message: $"references pattern {index}, but only {effectivePatternCount} pattern(s) are declared."));
            }
        }

        foreach (var (name, effect) in (document.Effects ?? new Dictionary<string, AudioEffectDocument>())) {
            ValidateRows(errors: errors, path: $"effects.{name}.rows", rows: effect.Rows);
        }

        DocumentCanonicalizer.ValidateExtensions(
            addError: (path, message) => errors.Add(item: new(Path: path, Message: message)),
            extensions: document.Extensions,
            knownMemberNames: KnownMemberNames
        );

        return errors;
    }

    /// <summary>Runs <see cref="Validate"/> and throws when it finds anything.</summary>
    /// <param name="document">The document to validate.</param>
    /// <param name="source">An optional source label (a file path or save handle) for the exception message.</param>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a
    /// structural invariant.</exception>
    public static void ValidateOrThrow(AudioDocument document, string? source = null) {
        var errors = Validate(document: document);

        if (errors.Count > 0) {
            throw new DocumentValidationException(errors: errors, source: source);
        }
    }

    /// <summary>Normalizes an already-schema-valid document: clamps/defaults every optional member so a consumer
    /// never sees a null or an out-of-range value it has to reason about (the load-time half of the document
    /// doctrine). Idempotent — <c>Normalize(Normalize(x))</c> equals <c>Normalize(x)</c> — which is what makes a
    /// saved file's own reload round-trip byte-identically. Effects re-key in ordinal name order so two documents
    /// differing only in effect declaration order canonicalize to the same bytes (the one dictionary-shaped member —
    /// declared record order covers everything else). Does NOT itself validate; callers cross
    /// <see cref="ValidateOrThrow"/> first (<see cref="Canonicalize"/> always does).</summary>
    /// <param name="document">The document to normalize.</param>
    /// <returns>The normalized document (unknown-member <see cref="AudioDocument.Extensions"/> ride along).</returns>
    public static AudioDocument Normalize(AudioDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var patterns = new List<IReadOnlyList<AudioRowDocument>>(capacity: (document.Patterns?.Count ?? 1));

        foreach (var pattern in (document.Patterns ?? [])) {
            patterns.Add(item: NormalizeRows(rows: pattern));
        }

        if (patterns.Count == 0) {
            patterns.Add(item: NormalizeRows(rows: null));
        }

        var order = new List<int>(capacity: (document.Order?.Count ?? patterns.Count));

        foreach (var index in (document.Order ?? Enumerable.Range(start: 0, count: patterns.Count))) {
            order.Add(item: index);
        }

        if (order.Count == 0) {
            order.Add(item: 0);
        }

        var effects = new Dictionary<string, AudioEffectDocument>(comparer: StringComparer.Ordinal);

        foreach (var name in (document.Effects?.Keys ?? []).Order(comparer: StringComparer.Ordinal)) {
            var effect = document.Effects![name];

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

    /// <summary>THE full pipeline: validates schema + structural invariants (throwing on either), normalizes the
    /// self-heal, then serializes to canonical UTF-8 bytes and hashes them through
    /// <see cref="DocumentCanonicalizer.Canonicalize"/>. Two calls against value-equal input documents always
    /// produce byte-identical bytes and therefore the same hash — the identity contract an inline-canonical world
    /// row pins.</summary>
    /// <param name="document">The document to canonicalize.</param>
    /// <param name="source">An optional source label (a file path or save handle) for a validation-failure message.</param>
    /// <returns>The validated, normalized document plus its canonical bytes and hash.</returns>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a
    /// structural invariant.</exception>
    public static CanonicalDocument<AudioDocument> Canonicalize(AudioDocument document, string? source = null) {
        ValidateOrThrow(document: document, source: source);

        return DocumentCanonicalizer.Canonicalize(document: Normalize(document: document));
    }

    private static void ValidateRows(List<DocumentValidationError> errors, string path, IReadOnlyList<AudioRowDocument>? rows) {
        for (var i = 0; i < (rows?.Count ?? 0); i++) {
            if (string.IsNullOrWhiteSpace(value: rows![i].Note)) {
                errors.Add(item: new(Path: $"{path}[{i}].note", Message: "a row's note may not be empty (use \"---\" to hold/rest)."));
            }
        }
    }

    // Rows normalize to a canonical note text (trimmed, upper-invariant) and a clamped duty; an absent/empty row
    // list becomes the 16-row silent pattern so a blank document is playable without special cases downstream.
    private static List<AudioRowDocument> NormalizeRows(IReadOnlyList<AudioRowDocument>? rows) {
        var normalized = new List<AudioRowDocument>(capacity: (rows?.Count ?? AudioDocument.DefaultPatternRowCount));

        foreach (var row in (rows ?? [])) {
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

/// <summary>
/// Loads and saves <see cref="AudioDocument"/>s against a tunes folder, as indented camel-case JSON through the ONE
/// shared <see cref="DocumentJsonOptions.Shared"/> instance. Root paths are explicit parameters — this library never
/// bakes in a working-directory convention; <see cref="DefaultFolder"/>/<see cref="DefaultCasRoot"/> document the
/// conventional roots a caller passes. Both <see cref="Save"/> and
/// <see cref="Load"/> ride <see cref="AudioCanonicalizer"/> exclusively — this store never validates, normalizes, or
/// hashes a document by any other route.
/// </summary>
public static class AudioDocumentStore {
    /// <summary>The conventional tunes folder name a host passes (relative to its working directory, the
    /// audio-document sibling of <see cref="CreationStore.DefaultFolder"/>) — documented, not implied: every method
    /// still takes the root explicitly.</summary>
    public const string DefaultFolder = "tunes";

    /// <summary>The conventional content-addressed store root a host passes — documented, not implied.</summary>
    public const string DefaultCasRoot = "store";

    /// <summary>Serializes a document to indented camel-case JSON.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The JSON text.</returns>
    public static string ToJson(AudioDocument document) =>
        JsonSerializer.Serialize(options: DocumentJsonOptions.Shared, value: document);

    /// <summary>Builds a fresh blank document (one silent pattern, default tempo) through the SAME normalize path
    /// every loaded document takes — never a hand-built record — so a brand-new working document can never drift
    /// from what a round-tripped save/load would produce. No file I/O.</summary>
    /// <param name="name">The new document's display name (null = "UNTITLED").</param>
    /// <returns>The normalized blank document.</returns>
    public static AudioDocument Blank(string? name = null) =>
        AudioCanonicalizer.Normalize(document: new AudioDocument(
            Effects: null,
            Name: name,
            Order: null,
            Patterns: null,
            Schema: AudioDocument.CurrentSchema,
            Tempo: null
        ));

    /// <summary>Saves a document under <c>&lt;tunesRoot&gt;/&lt;name&gt;.audio.json</c> (the file name is sanitized
    /// to letters, digits, dashes, and underscores; the document's display <see cref="AudioDocument.Name"/> is NOT
    /// rewritten — it is a title, not a handle) as <see cref="AudioCanonicalizer.Canonicalize"/>'s canonical bytes,
    /// and lands those same bytes in the content-addressed store under <c>refs/tunes/&lt;name&gt;</c> so a saved
    /// tune is immediately addressable by name.</summary>
    /// <param name="document">The document to save.</param>
    /// <param name="name">The save handle.</param>
    /// <param name="tunesRoot">The tunes folder (conventionally <see cref="DefaultFolder"/>).</param>
    /// <param name="casRoot">The content-addressed store root (conventionally <see cref="DefaultCasRoot"/>).</param>
    /// <returns>The written path.</returns>
    /// <exception cref="DocumentValidationException"><paramref name="document"/> fails
    /// <see cref="AudioCanonicalizer.Validate"/> (only a structural invariant can fail here, since this stamps the
    /// current schema before validating).</exception>
    public static string Save(AudioDocument document, string name, string tunesRoot, string casRoot) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(tunesRoot);
        ArgumentException.ThrowIfNullOrEmpty(casRoot);

        var sanitized = Sanitize(name: name);
        var path = PathFor(name: name, tunesRoot: tunesRoot);
        var canonical = AudioCanonicalizer.Canonicalize(document: (document with { Schema = AudioDocument.CurrentSchema }));

        _ = Directory.CreateDirectory(path: tunesRoot);
        File.WriteAllBytes(path: path, bytes: canonical.Bytes);

        var store = new Puck.Assets.ContentAddressedStore(root: casRoot);
        var hash = store.Put(content: canonical.Bytes);

        store.SetRef(category: "tunes", hash: hash, name: sanitized);

        return path;
    }

    /// <summary>Loads a tune by save handle or file path, strictly: <see cref="AudioCanonicalizer.ValidateOrThrow"/>
    /// rejects an absent/foreign schema or a structural invariant violation loudly rather than silently relabeling
    /// or repairing it, and only a document that PASSES validation is normalized — never trust persisted derived
    /// values.</summary>
    /// <param name="nameOrPath">The save handle (resolved under <paramref name="tunesRoot"/>) or an explicit file path.</param>
    /// <param name="tunesRoot">The tunes folder a bare handle resolves against (conventionally <see cref="DefaultFolder"/>).</param>
    /// <returns>The normalized document, or null when nothing readable exists at the location.</returns>
    /// <exception cref="InvalidDataException">The file deserialized to a null document, OR the document declares an
    /// absent/foreign schema or fails a structural invariant — in the latter two cases the offending
    /// <see cref="DocumentValidationException"/> rides as <see cref="Exception.InnerException"/>, so the repo's
    /// existing malformed-input catch convention (<c>Puck.Commands.CommandArgs.IsMalformedInput</c>) needs no
    /// changes to catch it.</exception>
    public static AudioDocument? Load(string nameOrPath, string tunesRoot) {
        ArgumentException.ThrowIfNullOrEmpty(nameOrPath);
        ArgumentException.ThrowIfNullOrEmpty(tunesRoot);

        var path = (File.Exists(path: nameOrPath) ? nameOrPath : PathFor(name: nameOrPath, tunesRoot: tunesRoot));

        if (!File.Exists(path: path)) {
            return null;
        }

        var json = File.ReadAllText(path: path);
        var document = (JsonSerializer.Deserialize<AudioDocument>(json: json, options: DocumentJsonOptions.Shared)
            ?? throw new InvalidDataException(message: $"'{path}' deserialized to null."));

        try {
            AudioCanonicalizer.ValidateOrThrow(document: document, source: path);
        } catch (DocumentValidationException exception) {
            throw new InvalidDataException(message: exception.Message, innerException: exception);
        }

        return AudioCanonicalizer.Normalize(document: document);
    }

    /// <summary>Lists the save handles under <paramref name="tunesRoot"/>.</summary>
    /// <param name="tunesRoot">The tunes folder (conventionally <see cref="DefaultFolder"/>).</param>
    /// <returns>The handles, sorted ordinally.</returns>
    public static IReadOnlyList<string> List(string tunesRoot) {
        ArgumentException.ThrowIfNullOrEmpty(tunesRoot);

        if (!Directory.Exists(path: tunesRoot)) {
            return [];
        }

        var names = new List<string>();

        foreach (var path in Directory.EnumerateFiles(path: tunesRoot, searchPattern: "*.audio.json")) {
            names.Add(item: Path.GetFileName(path: path)[..^".audio.json".Length]);
        }

        names.Sort(comparer: StringComparer.Ordinal);

        return names;
    }

    private static string PathFor(string name, string tunesRoot) =>
        Path.Combine(path1: tunesRoot, path2: $"{Sanitize(name: name)}.audio.json");

    // Mirrors CreationStore.Sanitize exactly (non [A-Za-z0-9_-] chars -> '-'; empty -> a safe default).
    private static string Sanitize(string name) {
        var builder = new System.Text.StringBuilder(capacity: name.Length);

        foreach (var character in name) {
            _ = builder.Append(value: ((char.IsAsciiLetterOrDigit(c: character) || (character is '-' or '_')) ? character : '-'));
        }

        return ((builder.Length > 0) ? builder.ToString() : "tune");
    }
}
