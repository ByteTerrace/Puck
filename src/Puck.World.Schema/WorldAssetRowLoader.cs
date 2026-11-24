using System.Text.Json;
using Puck.Assets;
using Puck.Assets.Documents;
using Puck.World.Authoring;

namespace Puck.World;

/// <summary>
/// Loads the document a name/source/hash asset reference row names: <see cref="WorldMusicRow"/>,
/// <see cref="TableRow"/>, <see cref="WorldTune"/> and <see cref="WorldPatch"/>. A row of a document loaded from a
/// directory resolves its <c>source</c> beside that document (<see cref="WorldDocumentPaths"/>). A row of a document
/// with no directory, which is what another process delivers, is read by its <c>hash</c> alone through the process's
/// content resolver (<see cref="WorldProjectionContent.Resolver"/>), and its <c>source</c> is a label that is never
/// opened. Shared by <see cref="WorldDefinitionValidator"/> (which also hash-pins and vocabulary-checks the loaded
/// document) and <c>Server.WorldServer</c> (which compiles it), so the load step has exactly one source of truth.
/// </summary>
public static class WorldAssetRowLoader {
    /// <summary>The most bytes the canonical document of one asset row holds when it is read by pin.</summary>
    public const int MaxPinnedRowBytes = (1 << 20);

    /// <summary>Returns the function that gives a family's canonical bytes from a document file's bytes: the bytes a
    /// row's <c>hash</c> names, which are not the file as written.</summary>
    /// <param name="family">The row's family, one of <see cref="AssetRowFamilies"/>.</param>
    /// <param name="name">The row's name, quoted by a canonicalizer's refusal.</param>
    /// <returns>A function that answers the canonical bytes, or <see langword="null"/> for bytes that do not parse and
    /// canonicalize as the family's document.</returns>
    public static Func<ReadOnlyMemory<byte>, byte[]?> Canonical(string family, string name) => bytes => Canonicalized(bytes: bytes, family: family, name: name).Bytes;

    /// <summary>Returns whether bytes are a family's canonical document: they parse, canonicalize, and canonicalize to
    /// themselves. It is the check content fetched from another process passes before it is stored.</summary>
    /// <param name="family">The row's family, one of <see cref="AssetRowFamilies"/>.</param>
    /// <param name="bytes">The candidate bytes.</param>
    /// <returns><see langword="true"/> when the bytes are the family's canonical form.</returns>
    public static bool IsCanonical(string family, ReadOnlyMemory<byte> bytes) =>
        ((Canonical(family: family, name: family)(arg: bytes) is { } canonical) && canonical.AsSpan().SequenceEqual(other: bytes.Span));

    /// <summary>Returns whether this process holds the content a row's pin names, so a read by pin has somewhere to
    /// look. It opens nothing.</summary>
    /// <param name="hash">The row's <c>hash</c>.</param>
    /// <returns><see langword="true"/> when the pin parses and a file or a store holds it.</returns>
    public static bool IsHeld(string hash) => (TryPin(hash: hash, pin: out var pin) && WorldProjectionContent.Resolver.Contains(pin: pin));
    /// <summary>Reads a row's <c>hash</c> as a content pin. An asset row carries the bare 64 digits of its canonical
    /// document's digest, and a pin's own text form is accepted beside it.</summary>
    /// <param name="hash">The row's <c>hash</c>.</param>
    /// <param name="pin">The pin, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the hash names a pin.</returns>
    public static bool TryPin([System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] string? hash, out ContentPin pin) {
        if (hash is null) {
            pin = default;

            return false;
        }

        return (ContentPin.TryParseHex(hex: hash, pin: out pin) || ContentPin.TryParse(pin: out pin, text: hash));
    }

    /// <summary>Computes the canonical content pin of an asset reference row's source document: the digest every
    /// reference row of its family carries as <c>hash</c>, so an author writes <c>hash: auto</c> and the compiler fills
    /// it with this.</summary>
    /// <param name="family">The row's family, one of <see cref="AssetRowFamilies"/>.</param>
    /// <param name="documentDirectory">The directory of the document that authored the row.</param>
    /// <param name="name">The row's name, quoted by a refusal.</param>
    /// <param name="source">The row's authored source path.</param>
    /// <param name="pin">The SHA-256 hex64 of the canonical bytes, when this method returns <see langword="true"/>.</param>
    /// <param name="error">Why the document did not load or canonicalize, when this method returns <see langword="false"/>.</param>
    /// <returns>Whether the source loaded and canonicalized.</returns>
    public static bool TryCanonicalHash(string family, string? documentDirectory, string name, string source, out string pin, out string error) {
        var loaded = (TryReadBytes(bytes: out var bytes, documentDirectory: documentDirectory, error: out var unread, hash: null, source: source)
            ? Canonicalized(bytes: bytes, family: family, name: name)
            : Loaded.Failed(unread));

        pin = (loaded.Hash ?? string.Empty);
        error = (loaded.Error ?? string.Empty);

        return (loaded.Hash is not null);
    }

    // One row's loaded canonical content, or why it did not load. The family's document type and canonicalizer are
    // named here once, for every consumer of a row's canonical hash.
    private readonly record struct Loaded(string? Hash, byte[]? Bytes, string? Error) {
        public static Loaded From<T>(CanonicalDocument<T> canonical) => new(canonical.Hash, canonical.Bytes, null);
        public static Loaded Failed(string? error) => new(null, null, error);
    }

    // The one table of the families' document types and canonicalizers: a document's bytes to its canonical form.
    private static Loaded Canonicalized(string family, string name, ReadOnlyMemory<byte> bytes) => family switch {
        AssetRowFamilies.Music => Canonicalized<MusicDocument>(bytes, document => Loaded.From(MusicCanonicalizer.Canonicalize(document: document, source: name))),
        AssetRowFamilies.Patch => Canonicalized<SynthPatchDocument>(bytes, document => Loaded.From(SynthPatchCanonicalizer.Canonicalize(document: document, source: name))),
        AssetRowFamilies.Table => Canonicalized<TableDocument>(bytes, document => Loaded.From(TableCanonicalizer.Canonicalize(document: document, source: name))),
        AssetRowFamilies.Tune => Canonicalized<AudioDocument>(bytes, document => Loaded.From(AudioCanonicalizer.Canonicalize(document: document, source: name))),
        _ => Loaded.Failed($"'{family}' is not an asset row family."),
    };
    private static Loaded Canonicalized<TDocument>(ReadOnlyMemory<byte> bytes, Func<TDocument, Loaded> canonicalize) where TDocument : class {
        try {
            return ((JsonSerializer.Deserialize<TDocument>(utf8Json: bytes.Span, options: DocumentJsonOptions.Shared) is { } document)
                ? canonicalize(document)
                : Loaded.Failed("the document parsed to nothing."));
        } catch (JsonException exception) {
            return Loaded.Failed($"the document is not valid {typeof(TDocument).Name} JSON: {exception.Message}");
        } catch (Exception exception) when (exception is DocumentValidationException or InvalidOperationException) {
            return Loaded.Failed(exception.Message.ReplaceLineEndings(replacementText: " "));
        }
    }

    private static bool TryLoad<TDocument>(string? documentDirectory, string source, out TDocument? document, out string? error, string? hash) where TDocument : class {
        document = null;
        if (!TryReadBytes(bytes: out var bytes, documentDirectory: documentDirectory, error: out error, hash: hash, source: source)) {
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
    // Reads a row's document bytes: by its pin when the document has no directory, else from the file beside it.
    private static bool TryReadBytes(string? documentDirectory, string source, string? hash, out byte[] bytes, out string? error) {
        bytes = [];

        if (string.IsNullOrWhiteSpace(value: source)) {
            error = "source is required.";

            return false;
        }

        if ((documentDirectory is null) && !Path.IsPathRooted(path: source)) {
            // A document with no directory names no file: the row's pin is its content, and the path is a label.
            if (!TryPin(hash: hash, pin: out var pin)) {
                error = $"source '{source}' is relative, this document has no directory to resolve it beside, and the row carries no content pin to read it by.";

                return false;
            }
            if (!WorldProjectionContent.Resolver.TryRead(content: out var pinned, maximumBytes: MaxPinnedRowBytes, pin: pin)) {
                error = $"source '{source}' is read by its pin {pin}, which this process does not hold.";

                return false;
            }
            bytes = pinned.ToArray();
        } else {
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

            try {
                bytes = File.ReadAllBytes(path: resolved);
            } catch (IOException exception) {
                error = $"source '{source}' could not be read: {exception.Message}";

                return false;
            }
        }

        error = null;

        return true;
    }

    /// <summary>Lists every asset row a definition declares, each with the file family it reads: <c>music</c>,
    /// <c>table</c>, <c>tune</c>, and <c>patch</c>, in that order and in declaration order within a family.</summary>
    /// <param name="definition">The definition to list.</param>
    /// <returns>The family, name, and authored source of each row; a <see langword="null"/> row is skipped.</returns>
    public static IEnumerable<(string Family, string Name, string Source)> Rows(WorldDefinition definition) {
        foreach (var (family, name, source, _) in PinnedRows(definition: definition)) {
            yield return (family, name, source);
        }
    }
    /// <summary>Lists every asset row a definition declares with its content pin, in <see cref="Rows"/>' order: the
    /// rows whose content travels by pin.</summary>
    /// <param name="definition">The definition to list.</param>
    /// <returns>The family, name, authored source and <c>hash</c> of each row; a <see langword="null"/> row is
    /// skipped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IEnumerable<(string Family, string Name, string Source, string Hash)> PinnedRows(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return PinnedRows(music: definition.Music, patches: definition.Patches, tables: definition.Tables, tunes: definition.Tunes);
    }
    /// <summary>Lists the asset rows of each family with their content pins, in <see cref="Rows"/>' order.</summary>
    /// <param name="music">The score rows, or <see langword="null"/>.</param>
    /// <param name="tables">The table rows, or <see langword="null"/>.</param>
    /// <param name="tunes">The tune rows, or <see langword="null"/>.</param>
    /// <param name="patches">The patch rows, or <see langword="null"/>.</param>
    /// <returns>The family, name, authored source and <c>hash</c> of each row; a <see langword="null"/> row is
    /// skipped.</returns>
    public static IEnumerable<(string Family, string Name, string Source, string Hash)> PinnedRows(IReadOnlyList<WorldMusicRow>? music, IReadOnlyList<Puck.State.TableRow>? tables, IReadOnlyList<WorldTune>? tunes, IReadOnlyList<WorldPatch>? patches) {
        foreach (var row in (music ?? [])) {
            if (row is not null) { yield return (AssetRowFamilies.Music, row.Name, row.Source, row.Hash); }
        }
        foreach (var row in (tables ?? [])) {
            if (row is not null) { yield return (AssetRowFamilies.Table, row.Name, row.Source, row.Hash); }
        }
        foreach (var row in (tunes ?? [])) {
            if (row is not null) { yield return (AssetRowFamilies.Tune, row.Name, row.Source, row.Hash); }
        }
        foreach (var row in (patches ?? [])) {
            if (row is not null) { yield return (AssetRowFamilies.Patch, row.Name, row.Source, row.Hash); }
        }
    }
    /// <summary>Loads a <see cref="WorldMusicRow"/>'s referenced <c>puck.music.v1</c> document.</summary>
    /// <param name="documentDirectory">The directory of the document that authored the row
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a document with none, whose
    /// row is read by its pin.</param>
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
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a document with none, whose
    /// row is read by its pin.</param>
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
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a document with none, whose
    /// row is read by its pin.</param>
    /// <param name="row">The reference row.</param>
    /// <param name="document">The loaded document, when this method returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryLoadTable(string? documentDirectory, TableRow row, out TableDocument? document, out string? error) =>
        TryLoad(
            document: out document,
            documentDirectory: documentDirectory,
            error: out error,
            source: row.Source,
            hash: row.Hash
        );
    /// <summary>Loads a <see cref="WorldTune"/>'s referenced <c>puck.tune.v1</c> document.</summary>
    /// <param name="documentDirectory">The directory of the document that authored the row
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a document with none, whose
    /// row is read by its pin.</param>
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
