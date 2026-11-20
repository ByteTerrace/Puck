using System.Text.Json.Nodes;
using Puck.Abstractions;
using Puck.Assets;
using Puck.Assets.Documents;
using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Validation;

/// <summary>The same composed world as the one a finding indexes, composed again with the authored origin of every
/// value it retains.</summary>
/// <param name="Composed">The composed world, node for node the tree the findings index.</param>
/// <param name="Origins">The authored origins of <paramref name="Composed"/>'s values.</param>
internal sealed record WorldFindingProvenance(JsonObject Composed, WorldDocumentOrigins Origins);
/// <summary>Places a finding against a composed world on the source line that authored what it names, so each finding
/// reads against one file whichever source composed it. A refusal names a document path first
/// (<c>prototypes[3].doc.shapes[1].blend: …</c>); the locator reads it in the composed tree and finds its author: the
/// compiled source's own layer through its source map (which names the library a template came from), or else the
/// basis or import document whose authored value the composition retained (<see cref="WorldDocumentOrigins"/>), read
/// through that document's own source map. A finding the authoring source acknowledges with an <c>allow</c> line is
/// dropped there.</summary>
/// <param name="composed">The composed world the findings index.</param>
/// <param name="source">The compiled source's own lowered document, or <see langword="null"/> when
/// <paramref name="composed"/> is that document.</param>
/// <param name="sourceMap">The source map of <paramref name="source"/>, or <see langword="null"/> when none is
/// known; every finding then reports without a location.</param>
/// <param name="provenance">Composes the world again with its authored origins, called at most once and only for a
/// finding no layer of the source authors, or <see langword="null"/> when no origins can be had; such a finding then
/// reports without a location.</param>
/// <param name="sourcePath">The compiled source's own file, or <see langword="null"/> for a buffer.</param>
internal sealed class WorldFindingLocator(JsonObject composed, JsonObject? source, SourceMap? sourceMap, Func<WorldFindingProvenance?>? provenance, string? sourcePath) {
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Authored> AuthoredSources = new(comparer: PuckPaths.Comparer);

    private bool m_traced;
    private WorldFindingProvenance? m_provenance;

    // Another document's compile, kept for as long as the digest of its bytes is unchanged.
    private sealed record Authored(string Digest, SourceMap Map, SourceAcknowledgements Acknowledgements);

    private WorldFindingProvenance? Provenance() {
        if (!m_traced) {
            m_traced = true;
            m_provenance = provenance?.Invoke();
        }

        return m_provenance;
    }

    // The authored origin of `node`: its own when it is a value, or else the shallowest value under it with one, the
    // origin's pointer trimmed back up to `node`.
    private static bool TryAuthor(WorldDocumentOrigins origins, JsonNode node, out string documentPath, out string documentPointer) {
        var frontier = new Queue<(JsonNode Node, int Depth)>();

        frontier.Enqueue(item: (node, 0));
        while (frontier.TryDequeue(result: out var next)) {
            switch (next.Node) {
                case JsonValue value when origins.TryGetOrigin(origin: out var origin, value: value): {
                        var pointer = origin.JsonPointer;

                        for (var depth = 0; (depth < next.Depth); depth++) {
                            var last = pointer.LastIndexOf(value: '/');

                            pointer = ((last < 0) ? "" : pointer[..last]);
                        }
                        documentPath = origin.DocumentPath;
                        documentPointer = pointer;

                        return true;
                    }
                case JsonObject obj:
                    foreach (var member in obj) {
                        if (member.Value is { } child) { frontier.Enqueue(item: (child, (next.Depth + 1))); }
                    }

                    break;
                case JsonArray list:
                    foreach (var element in list) {
                        if (element is not null) { frontier.Enqueue(item: (element, (next.Depth + 1))); }
                    }

                    break;
            }
        }

        documentPath = "";
        documentPointer = "";

        return false;
    }
    // The authoring source is read through CompileInputs, so a placement rests on that source as the compile it serves
    // does, and a source whose stamp is unchanged is neither read nor compiled again.
    private static Authored? Compile(string path) {
        if (!path.EndsWith(value: ".puck", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        byte[]? content;
        string digest;

        try {
            if (CompileInputs.Digest(content: out content, path: path) is not { } current) {
                return null;
            }
            digest = current;

            lock (Gate) {
                if (AuthoredSources.TryGetValue(key: path, value: out var held) && string.Equals(a: held.Digest, b: digest, comparisonType: StringComparison.Ordinal)) {
                    return held;
                }
            }
            if (content is null) {
                content = CompileInputs.ReadAllBytes(path: path);
                digest = ContentPin.Compute(content: content).Hex;
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        }

        var compilation = WorldCompileSession.Shared.Compile(includeTests: false, source: Puck.Confinement.FileContent.Text(content: content), sourceMap: new SourceMap(), sourcePath: path);

        if ((compilation.Document is null) || (compilation.Json is null)) {
            return null;
        }

        var authored = new Authored(Acknowledgements: compilation.Acknowledgements, Digest: digest, Map: compilation.SourceMap);

        lock (Gate) {
            AuthoredSources[path] = authored;
        }

        return authored;
    }
    private bool IsRoot(string? path) => ((path is null) || ((sourcePath is not null) && PuckPaths.Comparer.Equals(x: PuckPaths.Normalize(path: path), y: PuckPaths.Normalize(path: sourcePath))));

    /// <summary>Returns the finding <paramref name="message"/> reports, placed on the line that authored what
    /// <paramref name="pointer"/> names; <see langword="null"/> when the authoring source acknowledges it with an
    /// <c>allow</c> line.</summary>
    /// <param name="code">The diagnostic code.</param>
    /// <param name="severity">The diagnostic severity.</param>
    /// <param name="message">The human-readable finding message.</param>
    /// <param name="pointer">The JSON pointer the finding is about, or <see langword="null"/>/empty for the whole
    /// document.</param>
    /// <returns>The located diagnostic, or <see langword="null"/> when it is acknowledged.</returns>
    public Diagnostic? Locate(string code, DiagnosticSeverity severity, string message, string? pointer) {
        var diagnostic = new Diagnostic(Code: code, Message: message, Severity: severity, Span: SourceSpan.None);

        if (sourceMap is null) {
            return diagnostic;
        }

        var composedPointer = (pointer ?? string.Empty);
        var layerPointer = (((source is null) || ReferenceEquals(objA: source, objB: composed))
            ? composedPointer
            : WorldDocumentBasis.TraceToLayer(composed: composed, layer: source, pointer: composedPointer));

        if ((layerPointer is { Length: > 1 }) && (sourceMap.OriginOf(jsonPointer: layerPointer) is { } own)) {
            return (diagnostic with { SourcePath = (IsRoot(path: own.SourcePath) ? null : own.SourcePath), Span = own.Span });
        }
        // Only now, for a finding the source's own layer does not author, is the world composed again with origins.
        if ((composedPointer.Length <= 1) || (Provenance() is not { } tracked) || (DocumentJsonPointer.Nearest(pointer: composedPointer, root: tracked.Composed) is not { } node) || !TryAuthor(documentPath: out var documentPath, documentPointer: out var documentPointer, node: node, origins: tracked.Origins) || IsRoot(path: documentPath)) {
            return diagnostic;
        }
        if (Compile(path: documentPath) is not { } authored) {
            return (diagnostic with { SourcePath = documentPath });
        }

        var located = ((documentPointer.Length > 1) ? authored.Map.OriginOf(jsonPointer: documentPointer) : null);
        var placed = (diagnostic with { SourcePath = (located?.SourcePath ?? documentPath), Span = (located?.Span ?? SourceSpan.None) });

        return (authored.Acknowledgements.Acknowledges(diagnostic: placed) ? null : placed);
    }
}
