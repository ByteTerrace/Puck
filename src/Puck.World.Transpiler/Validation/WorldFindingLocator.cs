using System.Text.Json.Nodes;
using Puck.Abstractions;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler.Validation;

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
/// <param name="origins">The authored origins the composition retained, or <see langword="null"/> when it kept none;
/// a finding no layer of the source authors then reports without a location.</param>
/// <param name="sourcePath">The compiled source's own file, or <see langword="null"/> for a buffer.</param>
internal sealed class WorldFindingLocator(JsonObject composed, JsonObject? source, SourceMap? sourceMap, WorldDocumentOrigins? origins, string? sourcePath) {
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Authored> AuthoredSources = new(comparer: PuckPaths.Comparer);

    // Another document's compile, kept for as long as its text is unchanged.
    private sealed record Authored(string Text, SourceMap Map, SourceAcknowledgements Acknowledgements);

    private static string PathToken(string message) {
        var colon = message.IndexOf(value: ':');

        return ((colon > 0)
            ? message[..colon].Trim()
            : (message.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ' ').FirstOrDefault() ?? ""));
    }
    // The nearest node to `pointer` a source map registered, walking up the pointer, or null when none is.
    private static SourceOrigin? Resolve(SourceMap map, string pointer) {
        while (pointer.Length > 1) {
            if (map.TryGetOrigin(jsonPointer: pointer, origin: out var origin)) {
                return origin;
            }

            var last = pointer.LastIndexOf(value: '/');

            if (last <= 0) {
                break;
            }
            pointer = pointer[..last];
        }

        return null;
    }
    // The deepest node of `root` on the way to `pointer`.
    private static JsonNode? NodeAt(JsonNode root, string pointer) {
        var node = root;

        foreach (var segment in pointer.Split(separator: '/').Skip(count: 1)) {
            var member = segment.Replace(oldValue: "~1", newValue: "/", comparisonType: StringComparison.Ordinal).Replace(oldValue: "~0", newValue: "~", comparisonType: StringComparison.Ordinal);
            var next = node switch {
                JsonObject obj => (obj.TryGetPropertyValue(propertyName: member, jsonNode: out var child) ? child : null),
                JsonArray list when (int.TryParse(s: member, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var index) && (index >= 0) && (index < list.Count)) => list[index: index],
                _ => null,
            };

            if (next is null) {
                break;
            }
            node = next;
        }

        return node;
    }
    // The authored origin of `node`: its own when it is a value, or else the shallowest value under it with one, the
    // origin's pointer trimmed back up to `node`.
    private bool TryAuthor(JsonNode node, out string documentPath, out string documentPointer) {
        var frontier = new Queue<(JsonNode Node, int Depth)>();

        frontier.Enqueue(item: (node, 0));
        while (frontier.TryDequeue(result: out var next)) {
            switch (next.Node) {
                case JsonValue value when origins!.TryGetOrigin(origin: out var origin, value: value): {
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
    private static Authored? Compile(string path) {
        if (!path.EndsWith(value: ".puck", comparisonType: StringComparison.OrdinalIgnoreCase) || !File.Exists(path: path)) {
            return null;
        }

        string text;

        try {
            text = File.ReadAllText(path: path);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return null;
        }

        lock (Gate) {
            if (AuthoredSources.TryGetValue(key: path, value: out var held) && string.Equals(a: held.Text, b: text, comparisonType: StringComparison.Ordinal)) {
                return held;
            }
        }

        var compilation = WorldCompiler.Compile(includeTests: false, source: text, sourceMap: new SourceMap(), sourcePath: path);

        if ((compilation.Document is null) || (compilation.Json is null)) {
            return null;
        }

        var authored = new Authored(Acknowledgements: SourceAcknowledgements.Of(document: compilation.Document, sourcePath: path), Map: compilation.SourceMap, Text: text);

        lock (Gate) {
            AuthoredSources[path] = authored;
        }

        return authored;
    }
    private bool IsRoot(string? path) => ((path is null) || ((sourcePath is not null) && PuckPaths.Comparer.Equals(x: PuckPaths.Normalize(path: path), y: PuckPaths.Normalize(path: sourcePath))));

    /// <summary>Returns the finding <paramref name="message"/> reports, placed on the line that authored what its path
    /// names; <see langword="null"/> when the authoring source acknowledges it with an <c>allow</c> line.</summary>
    /// <param name="code">The diagnostic code.</param>
    /// <param name="severity">The diagnostic severity.</param>
    /// <param name="message">The finding, its document path first.</param>
    /// <returns>The located diagnostic, or <see langword="null"/> when it is acknowledged.</returns>
    public Diagnostic? Locate(string code, DiagnosticSeverity severity, string message) {
        var diagnostic = new Diagnostic(Code: code, Message: message, Severity: severity, Span: SourceSpan.None);

        if (sourceMap is null) {
            return diagnostic;
        }

        var composedPointer = WorldDocumentPointers.ToJsonPointer(path: PathToken(message: message), table: WorldConstructs.Table);
        var layerPointer = (((source is null) || ReferenceEquals(objA: source, objB: composed))
            ? composedPointer
            : WorldDocumentBasis.TraceToLayer(composed: composed, layer: source, pointer: composedPointer));

        if ((layerPointer is { Length: > 1 }) && (Resolve(map: sourceMap, pointer: layerPointer) is { } own)) {
            return (diagnostic with { SourcePath = (IsRoot(path: own.SourcePath) ? null : own.SourcePath), Span = own.Span });
        }
        if ((origins is null) || (composedPointer.Length <= 1) || (NodeAt(pointer: composedPointer, root: composed) is not { } node) || !TryAuthor(documentPath: out var documentPath, documentPointer: out var documentPointer, node: node) || IsRoot(path: documentPath)) {
            return diagnostic;
        }
        if (Compile(path: documentPath) is not { } authored) {
            return (diagnostic with { SourcePath = documentPath });
        }

        var located = Resolve(map: authored.Map, pointer: documentPointer);
        var placed = (diagnostic with { SourcePath = (located?.SourcePath ?? documentPath), Span = (located?.Span ?? SourceSpan.None) });

        return (authored.Acknowledgements.Acknowledges(diagnostic: placed, rootPath: documentPath) ? null : placed);
    }
}
