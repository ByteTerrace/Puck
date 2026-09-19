using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Modules;
using Puck.Transpiler.Parsing;
using Puck.Transpiler.Ast;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler;

/// <summary>What a <c>.puck</c> world source's imports are worth to a compile.</summary>
public enum ImportHandling {
    /// <summary>Walk the import graph, reporting an unresolvable path or a cycle, and lower the root alone —
    /// <c>puck compile</c>'s own default.</summary>
    Validate = 0,
    /// <summary>Walk the graph and inline every imported statement into one standalone document
    /// (<c>puck compile --bundle</c>).</summary>
    Bundle = 1,
    /// <summary>Lower the document as it stands, without touching the file system. The only mode a source with no
    /// path of its own can use.</summary>
    Ignore = 2,
}
/// <summary>One <c>.puck</c> world source compiled: the tree it parsed to, the document it lowered to, the map from
/// that document's pointers back to the source, everything either stage reported, and the embedding texts the
/// lowering discovered.</summary>
/// <param name="Document">The parsed tree, or <see langword="null"/> when the source did not parse.</param>
/// <param name="Json">The canonical document, or <see langword="null"/> when nothing was lowered.</param>
/// <param name="SourceMap">JSON pointers back to the spans that authored them.</param>
/// <param name="Diagnostics">Every diagnostic the parse, the import walk and the lowering reported.</param>
/// <param name="DiscoveredEmbeddings">Authored texts per embedding space, the input <c>puck embed</c> writes a lock
/// from.</param>
/// <param name="TestWorlds">One generated test world per <c>test</c> block the source wrote, in written order.
/// <see cref="Json"/> carries no trace of them.</param>
public sealed record WorldCompilation(
    DocumentNode? Document,
    JsonObject? Json,
    SourceMap SourceMap,
    DiagnosticBag Diagnostics,
    IReadOnlyDictionary<string, HashSet<string>> DiscoveredEmbeddings,
    IReadOnlyList<WorldTestWorld> TestWorlds
) {
    /// <summary>Gets whether a document was lowered and nothing refused it.</summary>
    public bool Success => (!Diagnostics.HasErrors && (Json is not null));

    /// <summary>Returns the lowered document, refusing to hand back partial output after an error.</summary>
    /// <returns>The canonical document.</returns>
    /// <exception cref="InvalidOperationException">The compile reported an error, or lowered nothing.</exception>
    public JsonObject RequireJson() {
        if (!Success) {
            throw new InvalidOperationException(message: string.Join(
                separator: Environment.NewLine,
                values: Diagnostics.Select(selector: static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
            ));
        }

        return Json!;
    }
}
/// <summary>The one door into <c>puck.world.definition.v1</c> compilation: source text in, canonical document out.
/// <c>puck compile</c>, <c>puck lint</c>, <c>puck embed</c>, the game's boot path, the basis composer, the language
/// server and every test harness call this and nothing else, so no caller can hold a stage the others do not —
/// a parse without its import walk, a lowering without the embedding lock beside the source, a compile whose
/// diagnostics go nowhere.</summary>
/// <remarks>The stages themselves stay where they live: <see cref="PuckParser"/>, <see cref="ModuleResolver"/>,
/// <see cref="EmbeddingLock"/> and <see cref="WorldDocumentEmitter"/>, whose lowering is reachable only from inside
/// this assembly. This type owns the order they run in and the defaults they run under.</remarks>
public static class WorldCompiler {
    /// <summary>Compiles <paramref name="source"/> to its canonical world document.</summary>
    /// <param name="source">The <c>.puck</c> source text.</param>
    /// <param name="sourcePath">The file the text came from, when it came from one. It roots relative asset paths,
    /// the import walk, and the <c>.embeddings.json</c> lock beside it.</param>
    /// <param name="basePath">The directory relative assets resolve against; defaults to
    /// <paramref name="sourcePath"/>'s own directory.</param>
    /// <param name="imports">What the import graph is worth to this compile.</param>
    /// <param name="embeddings">An embedding lock to resolve authored vector text against; defaults to the one
    /// beside <paramref name="sourcePath"/>.</param>
    /// <param name="defaultSchema">The schema to assume when the source declares none.</param>
    /// <param name="diagnostics">The bag every stage reports into; a fresh one when omitted.</param>
    /// <param name="sourceMap">The map the lowering registers pointers in; a fresh one when omitted.</param>
    /// <param name="cancellationToken">Cancels evaluation and expansion.</param>
    /// <param name="vocabulary">The described vocabulary every stage parses and lowers against; the shipped
    /// <see cref="WorldDocumentVocabulary.Instance"/> when omitted.</param>
    /// <returns>The compilation.</returns>
    public static WorldCompilation Compile(
        string source,
        string? sourcePath = null,
        string? basePath = null,
        ImportHandling imports = ImportHandling.Validate,
        EmbeddingLock? embeddings = null,
        string? defaultSchema = null,
        DiagnosticBag? diagnostics = null,
        SourceMap? sourceMap = null,
        CancellationToken cancellationToken = default,
        WorldDocumentVocabulary? vocabulary = null
    ) {
        ArgumentNullException.ThrowIfNull(argument: source);

        diagnostics ??= new DiagnosticBag();
        sourceMap ??= new SourceMap();
        vocabulary ??= WorldDocumentVocabulary.Instance;

        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            source: source,
            defaultSchema: defaultSchema,
            diagnostics: diagnostics,
            vocabulary: vocabulary
        );
        var document = parseResult.Value;

        if (document is null) {
            return new WorldCompilation(
                Diagnostics: diagnostics,
                DiscoveredEmbeddings: EmptyEmbeddings,
                Document: null,
                Json: null,
                SourceMap: sourceMap,
                TestWorlds: []
            );
        }

        // An import is a path, so a source with no path of its own has no graph to walk whatever the caller asked
        // for. Saying so here keeps every caller from repeating the same null check.
        var effectiveImports = ((sourcePath is null)
            ? ImportHandling.Ignore
            : imports
        );

        if (effectiveImports == ImportHandling.Bundle) {
            var bundled = ModuleResolver.BundleDocument(
                diagnostics: diagnostics,
                rootDoc: document,
                rootPath: sourcePath!,
                vocabulary: vocabulary
            );

            if (bundled is not null) {
                document = bundled;
            }
        } else if (effectiveImports == ImportHandling.Validate) {
            ModuleResolver.ValidateImportGraph(
                diagnostics: diagnostics,
                rootDoc: document,
                rootPath: sourcePath!,
                vocabulary: vocabulary
            );
        }

        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            basePath: (basePath ?? ((sourcePath is null)
                ? null
                : Path.GetDirectoryName(path: sourcePath))),
            cancellationToken: cancellationToken,
            diagnostics: diagnostics,
            discoveredEmbeddings: out var discovered,
            document: document,
            embeddings: (embeddings ?? ((sourcePath is null)
                ? null
                : EmbeddingLock.TryLoad(rootSourcePath: sourcePath))),
            sourceMap: sourceMap,
            testStem: TestStem(sourcePath: sourcePath),
            testWorlds: out var testWorlds,
            vocabulary: vocabulary
        );

        return new WorldCompilation(
            Diagnostics: diagnostics,
            DiscoveredEmbeddings: discovered,
            Document: document,
            Json: loweringResult.Value,
            SourceMap: sourceMap,
            TestWorlds: testWorlds
        );
    }
    /// <summary>Reads <paramref name="path"/> and compiles it.</summary>
    /// <param name="path">The <c>.puck</c> source file.</param>
    /// <param name="imports">What the import graph is worth to this compile.</param>
    /// <param name="embeddings">An embedding lock to resolve authored vector text against; defaults to the one
    /// beside <paramref name="path"/>.</param>
    /// <param name="diagnostics">The bag every stage reports into; a fresh one when omitted.</param>
    /// <param name="sourceMap">The map the lowering registers pointers in; a fresh one when omitted.</param>
    /// <param name="cancellationToken">Cancels evaluation and expansion.</param>
    /// <returns>The compilation.</returns>
    /// <exception cref="IOException">The file could not be read.</exception>
    public static WorldCompilation CompileFile(
        string path,
        ImportHandling imports = ImportHandling.Validate,
        EmbeddingLock? embeddings = null,
        DiagnosticBag? diagnostics = null,
        SourceMap? sourceMap = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(argument: path);

        return Compile(
            cancellationToken: cancellationToken,
            diagnostics: diagnostics,
            embeddings: embeddings,
            imports: imports,
            source: File.ReadAllText(path: path),
            sourceMap: sourceMap,
            sourcePath: path
        );
    }

    // A source's stem with every compound extension dropped, so `chinese-checkers.puck` and
    // `pong.world.puck` both name their generated test worlds `<stem>--<slug>.world.json`.
    private static string TestStem(string? sourcePath) {
        if (sourcePath is null) {
            return "world";
        }

        var stem = Path.GetFileName(path: sourcePath);
        var dot = stem.IndexOf(value: '.');

        return ((dot > 0)
            ? stem[..dot]
            : stem
        );
    }

    private static readonly IReadOnlyDictionary<string, HashSet<string>> EmptyEmbeddings =
        new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal);
}
