using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Modules;

namespace Puck.World.Transpiler.Composition;

/// <summary>The one <see cref="IWorldDocumentSource"/> that resolves a <c>basis</c>/<c>imports</c> document name beside
/// the referrer to the <c>.puck</c> source there that emits that name, whatever the source's stem, compiled in memory,
/// and to its <c>.world.json</c> document otherwise (<see cref="WorldDocumentName"/>), before handing the bytes to
/// <see cref="WorldDefinitionFileSource.TryComposeChainWithImports"/>. A source carries exactly the names it emits
/// (<see cref="WorldCompilation.EmittedNames"/>): an ordinary source its own stem, a composition the worlds it declares
/// and not its stem unless it declares a world of that name, and a module library none. Which source emits a name is
/// read from the directory's one name index (<see cref="WorldSourceIndex"/>), which parses each source and compiles
/// none. A document file beside the source of its exact name that emits that name is never read; beside a source that
/// emits another name, it is the name's carrier. Two files that carry one name, other than a source beside its own
/// document, are refused by name (<see cref="TryCarriers"/> holds a whole directory to the same rule). Both the game boot path (<c>Puck.World.PuckWorldLoader</c>) and <c>puck compile --validate</c>
/// (<see cref="Validation.WorldSemanticValidator"/>) compose through this one implementation, so a <c>.puck</c>
/// source and the running game resolve a basis or import chain identically. Every source a name resolves to compiles
/// through <see cref="WorldCompileCache.Shared"/>, so an unchanged source is compiled once however often a load reads
/// or re-checks it. Resolved names use forward slashes on every platform.</summary>
public sealed class PuckDocumentComposer : IWorldDocumentSource {
    private PuckDocumentComposer() { }

    /// <summary>Gets the composer. It holds no state, so one instance serves every composition and is the one a
    /// host installs as its local document source (<see cref="WorldDefinitionFileSource.UseLocalDocuments"/>).</summary>
    public static PuckDocumentComposer Instance { get; } = new();
    /// <inheritdoc />
    public bool ResolvesFiles => true;

    // Compiles a source through the compile cache for what it emits. A source that cannot be read reports nothing
    // here: like a source that does not compile, it is left to the door that reads it to say why.
    private static bool TryCompileSource(string path, out WorldCompiledSource? compiled, out WorldCompilation? failure) {
        try {
            return WorldCompileCache.Shared.TryCompile(
                compiled: out compiled,
                failure: out failure,
                path: path
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            compiled = null;
            failure = null;

            return false;
        }
    }

    /// <summary>Enumerates the documents a directory carries, one per document name, as this composer resolves them:
    /// <see cref="WorldDocumentName.TryCarriers(IEnumerable{WorldDocumentCarrier}, out IReadOnlyList{WorldDocumentCarrier}, out string)"/>
    /// over every name the files there claim (<see cref="WorldSourceIndex"/>): each document file the name it spells,
    /// and each source exactly the names it emits, whatever its stem. A module library carries no document name, so a
    /// <c>.world.json</c> document named like it is its name's carrier, and a name differing from it only in letter case
    /// collides with nothing. A composition carries the worlds it declares, each a carrier sharing its file, and its
    /// own stem only when it declares a world of that name.</summary>
    /// <remarks>Nothing is compiled: each source is parsed to read what it declares. A source that does not parse
    /// cannot say what it emits, so it carries its stem, and the door that compiles it reports why.</remarks>
    /// <param name="directory">The directory to enumerate.</param>
    /// <param name="option">Whether subdirectories are enumerated.</param>
    /// <param name="carriers">On success, one carrier per document, in ordinal order of its path, then its name.</param>
    /// <param name="libraries">The module libraries: the sources that emit no document, forward-slashed, in ordinal
    /// order of their paths.</param>
    /// <param name="reason">The named refusal (<see cref="Puck.Assets.Documents.DocumentName.Collision"/>), or empty
    /// on success.</param>
    /// <returns><see langword="true"/> when no two files carrying a document name name one document.</returns>
    public static bool TryCarriers(string directory, SearchOption option, out IReadOnlyList<WorldDocumentCarrier> carriers, out IReadOnlyList<string> libraries, out string reason) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);

        var files = new List<(WorldDocumentClaims File, string Beside)>();
        var directories = new List<string> { directory };

        if (option == SearchOption.AllDirectories) {
            directories.AddRange(collection: Directory.EnumerateDirectories(
                enumerationOptions: new EnumerationOptions {
                    AttributesToSkip = 0,
                    RecurseSubdirectories = true,
                },
                path: directory,
                searchPattern: "*"
            ));
        }

        foreach (var listed in directories) {
            var relative = Path.GetRelativePath(
                path: listed,
                relativeTo: directory
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            );
            var beside = ((relative == ".")
                ? string.Empty
                : (relative + "/")
            );

            files.AddRange(collection: WorldSourceIndex.Claims(directory: listed).Select(selector: file => (file, beside)));
        }

        var claims = new List<WorldDocumentCarrier>();
        var modules = new List<string>();

        foreach (var (file, beside) in files.OrderBy(
            comparer: StringComparer.Ordinal,
            keySelector: static entry => entry.File.Path
        )) {
            if (file.Names.Count == 0) {
                modules.Add(item: file.Path);
            }

            claims.AddRange(collection: file.Names.Select(selector: name => new WorldDocumentCarrier(
                Name: (beside + name),
                Path: file.Path
            )));
        }

        libraries = [.. modules.Order(comparer: StringComparer.Ordinal)];

        return WorldDocumentName.TryCarriers(
            carriers: out carriers,
            claims: claims,
            reason: out reason
        );
    }

    // The one file carrying the document named `leaf` in `directory` (forward-slashed): the carriers rule
    // (WorldDocumentName.TryCarriers) over every claim to the name, ignoring case, that a file there makes
    // (WorldSourceIndex), whatever that file's stem. `carrier` is that claim, its path spelled as the file system spells
    // it, or null when none carries the name; `compiled`/`failure` are its compile when it is a source; `passed` says
    // why a source whose stem spells the name carries no document of it, for a refusal to name. The index reads the
    // directory's listing and every source's bytes through CompileInputs, and the carrier compiles through the compile
    // cache, so a compile that resolves a name here rests on everything that decided it.
    private static bool TrySelectCarrier(string directory, string leaf, out WorldDocumentCarrier? carrier, out WorldCompiledSource? compiled, out WorldCompilation? failure, out string? passed, out string reason) {
        carrier = null;
        compiled = null;
        failure = null;
        passed = null;

        var claims = new List<WorldDocumentCarrier>(capacity: 2);

        foreach (var file in WorldSourceIndex.Claims(directory: directory)) {
            var carrying = file.Names.Where(predicate: name => Puck.Assets.Documents.DocumentName.Comparer.Equals(
                x: name,
                y: leaf
            )).ToArray();

            if (carrying.Length > 0) {
                claims.AddRange(collection: carrying.Select(selector: name => new WorldDocumentCarrier(
                    Name: name,
                    Path: file.Path
                )));
            } else if (file.IsSource && Puck.Assets.Documents.DocumentName.Comparer.Equals(
                x: WorldDocumentName.OfSourceFile(path: Path.GetFileName(path: file.Path)),
                y: leaf
            )) {
                passed = ((file.Names.Count == 0)
                    ? $"its source {file.Path} is a module library, which emits no document"
                    : $"its source {file.Path} is a composition, which declares no world named '{leaf}'"
                );
            }
        }

        if (!WorldDocumentName.TryCarriers(
            carriers: out var carriers,
            claims: claims,
            reason: out reason
        )) {
            return false;
        }

        if (carriers.Count == 0) {
            return true;
        }

        carrier = carriers[0];

        if (carriers[0].IsSource) {
            _ = TryCompileSource(
                compiled: out compiled,
                failure: out failure,
                path: carriers[0].Path
            );
        }

        return true;
    }
    // The spelling a resolved file is named by: the spelling the reference resolved to wherever the file system reads
    // it as the file found there, so a name keeps the spelling its referrer gave it, and the file's own spelling
    // otherwise.
    private static string Spelled(string found, string spelled) => (Puck.Abstractions.PuckPaths.Comparer.Equals(
        x: found,
        y: spelled
    )
        ? spelled
        : found
    );

    /// <inheritdoc />
    /// <remarks>The name is resolved again (<see cref="TryRead"/>), so a read stands only while the same file still
    /// carries the name: a document file read earlier no longer stands once a source emitting its name has appeared
    /// beside it, since the source now wins or the pair is refused, and a source read earlier no longer stands once it
    /// no longer emits the name. A source is resolved under each name it now emits, since a composition carries several
    /// and none need be its stem, and stands when one of them still selects it and compiles to
    /// <paramref name="content"/>. A <c>.puck</c> source's document is read through the compile cache and compared, so
    /// an edit to a module it imports is seen as surely as an edit to the source itself, and an unchanged source is not
    /// compiled again.</remarks>
    public bool StillReads(string resolvedName, byte[] content) {
        try {
            var path = resolvedName.Replace(
                newChar: '/',
                oldChar: '\\'
            );

            if (!(WorldDocumentName.IsSourceFile(path: path) || WorldDocumentName.IsDocumentFile(path: path))) {
                return File.ReadAllBytes(path: path).AsSpan().SequenceEqual(other: content);
            }

            var slash = path.LastIndexOf(value: '/');
            var directory = ((slash < 0)
                ? "."
                : path[..slash]
            );
            var names = (WorldDocumentName.IsSourceFile(path: path)
                ? (WorldSourceIndex.Declaration(path: path)?.Names(sourcePath: path) ?? [])
                : [WorldDocumentName.OfDocumentFile(path: path[(slash + 1)..])]
            );

            foreach (var name in names) {
                if (
                    !TrySelectCarrier(
                        carrier: out var carrier,
                        compiled: out var compiled,
                        directory: directory,
                        failure: out _,
                        leaf: name,
                        passed: out _,
                        reason: out _
                    ) ||
                    (carrier is not { } found) ||
                    !Puck.Abstractions.PuckPaths.Comparer.Equals(
                        x: found.Path,
                        y: path
                    )
                ) {
                    continue;
                }

                if (found.IsSource
                    ? ((compiled?.DocumentNamed(name: found.Name) is { } document) && document.AsSpan().SequenceEqual(other: content))
                    : File.ReadAllBytes(path: path).AsSpan().SequenceEqual(other: content)) {
                    return true;
                }
            }

            return false;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return false;
        }
    }
    /// <summary>Composes <paramref name="rootBytes"/>' whole basis-and-imports graph, rooted beside
    /// <paramref name="rootResolvedPath"/>, compiling every basis/import whose <c>.puck</c> source exists along the
    /// way. Returns <paramref name="rootBytes"/>'s own re-parse (as <paramref name="composed"/> being
    /// <see langword="null"/>) when the root names neither — see
    /// <see cref="WorldDefinitionFileSource.TryComposeChainWithImports"/>.</summary>
    /// <param name="rootResolvedPath">The root document's own resolved path — a basis/import reference resolves
    /// relative to its directory.</param>
    /// <param name="rootBytes">The root document's already-read raw bytes (JSON, whether hand-authored or already
    /// lowered from a <c>.puck</c> source).</param>
    /// <param name="composed">The composed tree (basis/imports members stripped) on success.</param>
    /// <param name="chainBytes">The bytes of every file touched composing the root.</param>
    /// <param name="reason">The one-line refusal reason, or empty on success.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for the selected host catalog.</param>
    /// <param name="catalog">The selected host machine catalog used for provider rewriting, or null for structural composition.</param>
    /// <returns><see langword="true"/> when the graph composed (or the root names neither basis nor imports).</returns>
    public static bool TryComposeWorldDocument(
        string rootResolvedPath,
        byte[] rootBytes,
        out JsonObject? composed,
        out IReadOnlyList<byte[]> chainBytes,
        out string reason,
        string catalogFingerprint = "",
        IMachineValidationCatalog? catalog = null
    ) {
        return WorldDefinitionFileSource.TryComposeChainWithImports(
            chainBytes: out chainBytes,
            composed: out composed,
            reason: out reason,
            rootBytes: rootBytes,
            rootResolvedName: rootResolvedPath.Replace(
                newChar: '/',
                oldChar: '\\'
            ),
            source: Instance,
            catalogFingerprint: catalogFingerprint,
            catalog: catalog
        );
    }
    /// <inheritdoc />
    public bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
        content = null;
        referrerName = referrerName.Replace(
            newChar: '/',
            oldChar: '\\'
        );

        if (!WorldDefinitionFileSource.TryResolveDocumentBeside(
            documentPath: out var documentPath,
            name: name,
            reason: out reason,
            referrerName: referrerName,
            sourcePath: out var sourcePath
        )) {
            resolvedName = name;

            return false;
        }

        // The name resolves to the file that carries it among every claim in its directory (WorldSourceIndex): the source
        // that emits it, whatever its stem, and its document file otherwise. A source beside its own document wins, its
        // document a stale or foreign copy never read; a module library emits no name and a composition only the worlds
        // it declares, so a document file beside either one under another name is that name's carrier. A compile that
        // reads a basis through here rests on the directory's listing and every source there (CompileInputs).
        var slash = documentPath.LastIndexOf(value: '/');

        resolvedName = documentPath;

        try {
            if (!TrySelectCarrier(
                carrier: out var carrier,
                compiled: out var compiled,
                directory: documentPath[..slash],
                failure: out var failure,
                leaf: WorldDocumentName.OfDocumentFile(path: documentPath[(slash + 1)..]),
                passed: out var passed,
                reason: out var collision
            )) {
                reason = $"document '{name}' (named by {referrerName}) names more than one document: {collision}";

                return false;
            }

            if (carrier is not { } found) {
                reason = ((passed is null)
                    ? $"document '{name}' (named by {referrerName}) has neither a source at {sourcePath} nor a document at {documentPath}."
                    : $"document '{name}' (named by {referrerName}) has no document at {documentPath}, and {passed}."
                );

                return false;
            }

            resolvedName = Spelled(
                found: found.Path,
                spelled: (found.IsSource
                    ? sourcePath
                    : documentPath
                )
            );

            if (found.IsSource) {
                // A source that carries the name emits a document under it, so only a source that does not compile has
                // none; compiling it again is what says why, with the diagnostic every other door reports.
                if (compiled?.DocumentNamed(name: found.Name) is not { } document) {
                    var compilation = (failure ?? WorldCompiler.CompileFile(path: resolvedName));
                    var error = compilation.Diagnostics.FirstOrDefault(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));

                    reason = ((error is null)
                        ? $"{resolvedName} does not compile."
                        : $"{resolvedName}({error.Span.Line},{error.Span.Column}) does not compile: {error.Code} {error.Message.ReplaceLineEndings(replacementText: " ")}"
                    );

                    return false;
                }

                content = document;
            } else {
                content = CompileInputs.ReadAllBytes(path: resolvedName);
            }

            reason = string.Empty;

            return true;
        } catch (Exception ex) {
            reason = $"cannot read document {resolvedName}: {ex.Message}";

            return false;
        }
    }
}
