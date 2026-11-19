using System.Text.Json;
using Puck.Assets.Documents;
using Puck.World.Authoring;

namespace Puck.World;

/// <summary>
/// Resolves a name/source/hash asset reference row's document off disk — <see cref="WorldMusicRow.Source"/>, <see
/// cref="TableRow.Source"/>, <see cref="WorldTune.Source"/> and <see cref="WorldPatch.Source"/> resolve beside the
/// document that authored the row (<see cref="WorldDocumentPaths"/>). Shared by <see cref="WorldDefinitionValidator"/>
/// (which also hash-pins and vocabulary-checks the loaded document) and <c>Server.WorldServer</c> (which compiles it)
/// so the load step has exactly one source of truth.
/// </summary>
public static class WorldAssetRowLoader {
    private const string InlineJson = "data:application/json;base64,";
    // Bounds the embedding cache and the once-per-row failure reports; a full table is cleared and refilled, never
    // grown, since every entry is derived again from its row on a miss.
    private const int MaxEmbeddings = 4096;

    // A validated embedding by content pin: the canonical bytes of a document whose canonical hash is the pin, as the
    // inline source every delivery of that row carries. The key is the content's identity, so an entry can never go
    // stale; a miss loads, canonicalizes and checks the pin again.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(AudioFamily Family, string Hash), string> Embeddings = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(AudioFamily Family, string Name, string Hash), byte> Reported = new();

    /// <summary>The audio asset families a world delivers embedded.</summary>
    internal enum AudioFamily : byte {
        /// <summary>A <c>puck.synthesizer-patch.v1</c> row.</summary>
        Patch,
        /// <summary>A <c>puck.tune.v1</c> row.</summary>
        Tune,
        /// <summary>A <c>puck.music.v1</c> row.</summary>
        Music,
    }

    /// <summary>Whether the source is a self-contained audio asset rather than a filesystem path.</summary>
    public static bool IsEmbeddedAudio(string source) => source.StartsWith(InlineJson, StringComparison.Ordinal);

    /// <summary>Materializes the published audio assets so a remote or routed listener needs no authority filesystem
    /// path. A row whose document no longer loads or no longer matches its pin is dropped from the delivery and reported
    /// once on standard error, naming the row.</summary>
    /// <param name="definition">The authority's definition.</param>
    /// <returns>The definition with every deliverable audio row carrying its canonical content inline.</returns>
    public static WorldDefinition EmbedAudio(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var directory = definition.DocumentDirectory;

        return definition with {
            PatchesRaw = [.. definition.Patches.Select(patch => Embed(AudioFamily.Patch, patch.Name, patch.Source, patch.Hash,
                () => TryLoadPatch(directory, patch, out var document, out var error) ? Loaded.From(SynthPatchCanonicalizer.Canonicalize(document!)) : Loaded.Failed(error))
                is { } source ? patch with { Source = source } : null).OfType<WorldPatch>()],
            TunesRaw = [.. definition.Tunes.Select(tune => Embed(AudioFamily.Tune, tune.Name, tune.Source, tune.Hash,
                () => TryLoadTune(directory, tune, out var document, out var error) ? Loaded.From(AudioCanonicalizer.Canonicalize(document!)) : Loaded.Failed(error))
                is { } source ? tune with { Source = source } : null).OfType<WorldTune>()],
            Music = definition.Music?.Select(row => Embed(AudioFamily.Music, row.Name, row.Source, row.Hash,
                () => TryLoadMusic(directory, row, out var document, out var error) ? Loaded.From(MusicCanonicalizer.Canonicalize(document!)) : Loaded.Failed(error))
                is { } source ? row with { Source = source } : null).OfType<WorldMusicRow>().ToArray(),
        };
    }

    // One row's loaded canonical content, or why it did not load.
    private readonly record struct Loaded(string? Hash, byte[]? Bytes, string? Error) {
        public static Loaded From<T>(CanonicalDocument<T> canonical) => new(canonical.Hash, canonical.Bytes, null);
        public static Loaded Failed(string? error) => new(null, null, error);
    }

    private static string? Embed(AudioFamily family, string name, string source, string hash, Func<Loaded> load) {
        if (Embeddings.TryGetValue(key: (family, hash), value: out var known)) { return known; }
        if (IsEmbeddedAudio(source: source)) { return source; }
        var loaded = load();
        if ((loaded.Bytes is not { } bytes) || !string.Equals(loaded.Hash, hash, StringComparison.Ordinal)) {
            if (Reported.Count >= MaxEmbeddings) { Reported.Clear(); }
            if (Reported.TryAdd(key: (family, name, hash), value: 0)) {
                Console.Error.WriteLine(value: $"[world.audio: {family.ToString().ToLowerInvariant()}[{name}] is withheld from deliveries: {(loaded.Error ?? "its canonical content no longer matches its pin.")}]");
            }
            return null;
        }
        var embedded = InlineJson + Convert.ToBase64String(inArray: bytes);
        RememberEmbedding(family: family, hash: hash, source: embedded);
        return embedded;
    }

    /// <summary>Records an embedded source as the validated content of its pin.</summary>
    /// <param name="family">The asset family.</param>
    /// <param name="hash">The content pin.</param>
    /// <param name="source">The inline source.</param>
    internal static void RememberEmbedding(AudioFamily family, string hash, string source) {
        if (Embeddings.Count >= MaxEmbeddings) { Embeddings.Clear(); }
        Embeddings[(family, hash)] = source;
    }
    /// <summary>Determines whether an embedded source is one this process already validated for its pin.</summary>
    /// <param name="family">The asset family.</param>
    /// <param name="hash">The content pin.</param>
    /// <param name="source">The inline source.</param>
    /// <returns><see langword="true"/> when the source is byte-for-byte a validated embedding of the pin.</returns>
    internal static bool IsKnownEmbedding(AudioFamily family, string hash, string source) =>
        Embeddings.TryGetValue(key: (family, hash), value: out var known) && string.Equals(known, source, StringComparison.Ordinal);

    private static bool TryLoad<TDocument>(string? documentDirectory, string source, out TDocument? document, out string? error, string? hash = null) where TDocument : class {
        document = null;

        if (string.IsNullOrWhiteSpace(value: source)) {
            error = "source is required.";

            return false;
        }

        if (IsEmbeddedAudio(source)) {
            try {
                var embedded = Convert.FromBase64String(source[InlineJson.Length..]);
                if (!string.Equals(Puck.Assets.ContentPin.Compute(embedded).Hex, hash, StringComparison.Ordinal)) {
                    error = "embedded audio source does not match its content pin.";
                    return false;
                }
                document = JsonSerializer.Deserialize<TDocument>(embedded, DocumentJsonOptions.Shared);
                error = document is null ? "embedded audio source parsed to no document." : null;
                return document is not null;
            } catch (Exception exception) when (exception is FormatException or JsonException) {
                error = "embedded audio source is malformed.";
                return false;
            }
        }

        if (!WorldDocumentPaths.TryResolve(
            documentDirectory: documentDirectory,
            path: source,
            reason: out var unresolved,
            resolved: out var resolved
        )) {
            error = $"source {unresolved}.";

            return false;
        }

        if (!File.Exists(path: resolved)) {
            error = $"source '{source}' does not exist.";

            return false;
        }

        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path: resolved);
        } catch (IOException exception) {
            error = $"source '{source}' could not be read: {exception.Message}";

            return false;
        }

        WorldBootWork.Count(kind: WorldBootWork.AssetLoads);

        try {
            document = JsonSerializer.Deserialize<TDocument>(
                utf8Json: bytes,
                options: Puck.Assets.Documents.DocumentJsonOptions.Shared
            );
        } catch (JsonException exception) {
            error = $"source '{source}' is not valid {typeof(TDocument).Name} JSON: {exception.Message}";

            return false;
        }

        if (document is null) {
            error = $"source '{source}' parsed to no document.";

            return false;
        }

        error = null;

        return true;
    }

    /// <summary>Lists every asset row a definition declares, each with the file family it reads: <c>music</c>,
    /// <c>table</c>, <c>tune</c>, and <c>patch</c>, in that order and in declaration order within a family.</summary>
    /// <param name="definition">The definition to list.</param>
    /// <returns>The family, name, and authored source of each row; a <see langword="null"/> row is skipped.</returns>
    public static IEnumerable<(string Family, string Name, string Source)> Rows(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        foreach (var row in (definition.Music ?? [])) {
            if (row is not null) { yield return ("music", row.Name, row.Source); }
        }
        foreach (var row in (definition.Tables ?? [])) {
            if (row is not null) { yield return ("table", row.Name, row.Source); }
        }
        foreach (var row in definition.Tunes) {
            if (row is not null) { yield return ("tune", row.Name, row.Source); }
        }
        foreach (var row in definition.Patches) {
            if (row is not null) { yield return ("patch", row.Name, row.Source); }
        }
    }
    /// <summary>Loads a <see cref="WorldMusicRow"/>'s referenced <c>puck.music.v1</c> document.</summary>
    /// <param name="documentDirectory">The directory of the document that authored the row
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a document with none, which
    /// refuses a relative source by name.</param>
    /// <param name="row">The reference row.</param>
    /// <param name="document">The loaded document, when this method returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryLoadMusic(string? documentDirectory, WorldMusicRow row, out MusicDocument? document, out string? error) =>
        TryLoad(
            document: out document,
            documentDirectory: documentDirectory,
            error: out error,
            source: row.Source,
            hash: row.Hash
        );
    /// <summary>Loads a <see cref="WorldPatch"/>'s referenced <c>puck.synthesizer-patch.v1</c> document.</summary>
    /// <param name="documentDirectory">The directory of the document that authored the row
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a document with none, which
    /// refuses a relative source by name.</param>
    /// <param name="row">The reference row.</param>
    /// <param name="document">The loaded document, when this method returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryLoadPatch(string? documentDirectory, WorldPatch row, out SynthPatchDocument? document, out string? error) =>
        TryLoad(
            document: out document,
            documentDirectory: documentDirectory,
            error: out error,
            source: row.Source,
            hash: row.Hash
        );
    /// <summary>Loads a <see cref="TableRow"/>'s referenced <c>puck.table.v1</c> document.</summary>
    /// <param name="documentDirectory">The directory of the document that authored the row
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a document with none, which
    /// refuses a relative source by name.</param>
    /// <param name="row">The reference row.</param>
    /// <param name="document">The loaded document, when this method returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryLoadTable(string? documentDirectory, TableRow row, out TableDocument? document, out string? error) =>
        TryLoad(
            document: out document,
            documentDirectory: documentDirectory,
            error: out error,
            source: row.Source
        );
    /// <summary>Loads a <see cref="WorldTune"/>'s referenced <c>puck.tune.v1</c> document.</summary>
    /// <param name="documentDirectory">The directory of the document that authored the row
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a document with none, which
    /// refuses a relative source by name.</param>
    /// <param name="row">The reference row.</param>
    /// <param name="document">The loaded document, when this method returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryLoadTune(string? documentDirectory, WorldTune row, out AudioDocument? document, out string? error) =>
        TryLoad(
            document: out document,
            documentDirectory: documentDirectory,
            error: out error,
            source: row.Source,
            hash: row.Hash
        );
}
