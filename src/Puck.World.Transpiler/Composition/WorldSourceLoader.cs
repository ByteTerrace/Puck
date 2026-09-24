using System.Text;
using Puck.Abstractions.Machines;

namespace Puck.World.Transpiler.Composition;

/// <summary>Admits a single-document <c>.puck</c> world source the way the game boots one: its compiled document's
/// basis-and-imports graph composed beside the source, then one load and admission that proves its adjacency claims
/// against the documents beside it. The game's boot loader and the laws that pin a boot's work both go through
/// here.</summary>
public static class WorldSourceLoader {
    /// <summary>Gets the memory-only bake cache every compiled world this process derives fills: a compile writes its
    /// bake pack (<see cref="WorldBakePack"/>) from it, and a tree compile bakes a creation its worlds share
    /// once.</summary>
    public static WorldBakeStore Bakes { get; } = new();

    /// <summary>Composes and admits <paramref name="document"/>, the document <paramref name="path"/> compiled to.</summary>
    /// <param name="path">The full path of the <c>.puck</c> source; its basis, imports and neighbours resolve beside
    /// it.</param>
    /// <param name="document">The source's compiled document, UTF-8 (<see cref="WorldCompiledSource.Document"/>).</param>
    /// <param name="admission">The admitted document and its retained programs, or <see langword="null"/> on
    /// refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for the selected host catalog.</param>
    /// <param name="catalog">The selected host machine catalog used for provider composition and validation, or
    /// <see langword="null"/> to defer provider checks.</param>
    /// <param name="overrides">Rewrites the loaded document before its one admission, or <see langword="null"/> when
    /// the host overrides nothing the document carries.</param>
    /// <param name="compiled">The compiled world the drawn document is read from or written to, or
    /// <see langword="null"/> to draw the document without one.</param>
    /// <returns><see langword="true"/> when the document composed, loaded and was admitted.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static bool TryLoadForAdmission(string path, byte[] document, out WorldDefinitionAdmission? admission, out string reason, string catalogFingerprint = "",
        IMachineValidationCatalog? catalog = null, Func<WorldDefinition, WorldDefinition>? overrides = null, CompiledWorldRequest? compiled = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);
        ArgumentNullException.ThrowIfNull(argument: document);

        admission = null;

        if (!TryCompose(
            catalog: catalog,
            catalogFingerprint: catalogFingerprint,
            document: document,
            json: out var json,
            path: path,
            reason: out reason
        )) {
            return false;
        }

        var directory = WorldDocumentPaths.DirectoryOf(documentPath: path);

        return WorldDefinitionLoader.TryLoadForAdmission(
            admission: out admission,
            catalog: catalog,
            catalogFingerprint: catalogFingerprint,
            documentDirectory: directory,
            instanceIdentity: WorldDefinitionLoader.BootInstanceName,
            neighbours: new WorldFileNeighbourResolver(
                baseDirectory: () => directory,
                catalog: catalog,
                catalogFingerprint: catalogFingerprint
            ),
            overrides: overrides,
            compiled: compiled,
            reason: out reason,
            sourceName: path,
            utf8: json
        );
    }
    /// <summary>Composes <paramref name="document"/> beside <paramref name="path"/> and parses the result: the
    /// authored definition a boot of the document keys its compiled world by (<see cref="CompiledWorld.HeaderFor"/>),
    /// before any draw.</summary>
    /// <param name="path">The full path the document is read as: its <c>.puck</c> source or its <c>.world.json</c>
    /// file; its basis and imports resolve beside it.</param>
    /// <param name="document">The document, UTF-8.</param>
    /// <param name="authored">The parsed, composed, undrawn definition, or <see langword="null"/> on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for the selected host catalog.</param>
    /// <param name="catalog">The selected host machine catalog, or <see langword="null"/> to defer provider checks.</param>
    /// <returns><see langword="true"/> when the document composed and parsed.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static bool TryParseComposed(string path, byte[] document, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? authored, out string reason,
        string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);
        ArgumentNullException.ThrowIfNull(argument: document);

        authored = null;

        if (!TryCompose(
            catalog: catalog,
            catalogFingerprint: catalogFingerprint,
            document: document,
            json: out var json,
            path: path,
            reason: out reason
        ) || !WorldDefinitionFileSource.TryParseDocument(
            definition: out var parsed,
            json: Encoding.UTF8.GetString(bytes: json),
            reason: out reason,
            sourceName: path
        )) {
            return false;
        }

        authored = (parsed! with { DocumentDirectory = WorldDocumentPaths.DirectoryOf(documentPath: path) });
        return true;
    }
    /// <summary>Composes and parses <paramref name="document"/> (<see cref="TryParseComposed"/>) and derives its
    /// compiled world fresh for the boot instance (<see cref="CompiledWorld.TryCompile"/>): the file <c>puck compile</c>
    /// and the build write beside a document. It carries the standard chunks and the <c>BAKE</c> chunk
    /// (<see cref="WorldBakeChunk"/>), whose outcomes the derivation leaves in <see cref="Bakes"/> for the caller's bake
    /// pack.</summary>
    /// <param name="path">The full path the document is read as; its basis and imports resolve beside it.</param>
    /// <param name="document">The document, UTF-8.</param>
    /// <param name="compiledWorld">The compiled world, or <see langword="null"/> on refusal.</param>
    /// <param name="reason">Why the document has no compiled world, or empty on success.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for the selected host catalog.</param>
    /// <param name="catalog">The selected host machine catalog, or <see langword="null"/> to defer provider checks.</param>
    /// <param name="bakePack">Where the bake pack lies relative to the directory the compiled world ships in
    /// (<see cref="WorldBakePack.Reference"/>), or <see langword="null"/> for <see cref="WorldBakePack.FileName"/>
    /// beside it.</param>
    /// <returns><see langword="true"/> when the document composed, parsed, drew, and every chunk derived.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static bool TryCompileWorld(string path, byte[] document, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out byte[]? compiledWorld, out string reason,
        string catalogFingerprint = "", IMachineValidationCatalog? catalog = null, string? bakePack = null) {
        compiledWorld = null;

        return (TryParseComposed(
            authored: out var authored,
            catalog: catalog,
            catalogFingerprint: catalogFingerprint,
            document: document,
            path: path,
            reason: out reason
        ) && CompiledWorld.TryCompile(
            authored: authored,
            bytes: out compiledWorld,
            catalogFingerprint: catalogFingerprint,
            chunks: WorldBakeChunk.Register(
                chunks: CompiledWorldChunks.Standard,
                packReference: bakePack,
                store: Bakes
            ),
            instanceIdentity: WorldDefinitionLoader.BootInstanceName,
            reason: out reason,
            sourceName: path
        ));
    }

    private static bool TryCompose(string path, byte[] document, out byte[] json, out string reason, string catalogFingerprint, IMachineValidationCatalog? catalog) {
        json = document;

        if (!PuckDocumentComposer.TryComposeWorldDocument(
            catalog: catalog,
            catalogFingerprint: catalogFingerprint,
            chainBytes: out _,
            composed: out var composed,
            reason: out var composeReason,
            rootBytes: document,
            rootResolvedPath: path
        )) {
            reason = $"{path} composition refused: {composeReason}";

            return false;
        }

        if (composed is not null) {
            json = Encoding.UTF8.GetBytes(s: composed.ToJsonString());
        }

        reason = string.Empty;
        return true;
    }
}
