using System.Text;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

// A name the compiler generates is spelled the one way no author-written name may be (GeneratedName): the compiler
// records each one it writes into a document, and once the document has lowered every declared name in that form that
// it did not write is refused where the author wrote it.
public static partial class WorldDocumentEmitter {
    private const string GeneratedNamesAnnotation = "GeneratedNames";

    // The names this compilation generated into its documents. One set for the whole compilation: a module expansion
    // shares it (CreateModuleAnnotations), so a name generated inside a module is known to the document it merges into.
    private static HashSet<string> GetOrCreateGeneratedNames(DocumentScope scope) {
        if (
            !scope.Annotations.TryGetValue(
            key: GeneratedNamesAnnotation,
            value: out var held
        ) ||
            (held is not HashSet<string> names)
        ) {
            names = new HashSet<string>(comparer: StringComparer.Ordinal);
            scope.Annotations[GeneratedNamesAnnotation] = names;
        }

        return names;
    }
    // Records a name this compilation generated and returns it.
    private static string Generated(string name, DocumentScope scope) {
        if (GeneratedName.IsGenerated(name: name)) {
            _ = GetOrCreateGeneratedNames(scope: scope).Add(item: name);
        }

        return name;
    }
    /// <summary>Reports PUCK113 for every name <paramref name="document"/> declares in either reserved spelling that
    /// this compilation did not generate: every name <see cref="WorldAuthoredNames"/> reads — a declaration of any
    /// namespace the name registry lists, a row's <c>id</c>, a declared cell's <c>key</c>, a reference's or
    /// destination's <c>name</c>.</summary>
    /// <param name="document">The lowered document, before any link or test adds what those generate.</param>
    /// <param name="scope">The scope the document lowered in.</param>
    private static void RefuseAuthoredGeneratedNames(JsonObject document, DocumentScope scope) {
        var generated = GetOrCreateGeneratedNames(scope: scope);

        WorldAuthoredNames.Visit(
            document: document,
            visitor: (node, name) => {
                if (
                    generated.Contains(item: name) ||
                    IsImportQualified(document: document, name: name) ||
                    GeneratedName.TryValidateAuthored(
                    name: name,
                    reason: out var reason
                )
                ) {
                    return;
                }

                var span = SourceSpan.None;

                _ = scope.SourceMap?.TryGetSpan(
                    jsonPointer: PointerOf(node: node),
                    span: out span
                );
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.GeneratedNameReserved,
                    message: reason,
                    span: span
                );
            }
        );
    }

    /// <summary>Returns whether <paramref name="name"/> is one a document's own aliased import declares: headed by
    /// the <c>as</c> of one of its <c>imports</c> entries (<c>dive$diveCourt</c> under <c>{"as": "dive"}</c>). The
    /// composer generates the name, and the importing document restates or reads it in that spelling.</summary>
    /// <param name="document">The importing document.</param>
    /// <param name="name">The name.</param>
    /// <returns><see langword="true"/> when the name's head is one of the document's import aliases.</returns>
    internal static bool IsImportQualified(JsonObject document, string name) {
        if ((document["imports"] is not JsonArray imports) || !GeneratedName.IsGenerated(name: name)) {
            return false;
        }
        foreach (var entry in imports.OfType<JsonObject>()) {
            if ((entry[WorldImport.AsMemberName] is JsonValue alias) && alias.TryGetValue<string>(value: out var head) &&
                GeneratedName.TryStripHead(head: head, name: name, rest: out _)) {
                return true;
            }
        }
        return false;
    }
    // The JSON pointer a lowered node sits at, the key the source map registers spans under.
    internal static string PointerOf(JsonNode node) {
        var segments = new List<string>();

        for (var current = node; (current.Parent is { } parent); current = parent) {
            segments.Add(item: ((parent is JsonArray)
                ? current.GetElementIndex().ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
                : current.GetPropertyName()
            ));
        }

        var pointer = new StringBuilder();

        for (var index = (segments.Count - 1); (index >= 0); index--) {
            _ = pointer.Append(value: '/').Append(value: segments[index]);
        }

        return pointer.ToString();
    }
}
