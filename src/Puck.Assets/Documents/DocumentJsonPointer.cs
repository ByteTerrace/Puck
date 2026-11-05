using System.Globalization;
using System.Text.Json.Nodes;

namespace Puck.Assets.Documents;

/// <summary>The one reading of an RFC 6901 JSON pointer into a document tree: the escaping of a member name, the segments
/// of a pointer, one step into a node, the node a pointer names (as a try-form or a refusal), the deepest node that
/// exists on the way to one that does not, and the walk that names every scalar. A caller that must walk two trees at
/// once, or build along a pointer, takes its steps from <see cref="TrySegments"/>, <see cref="TryChild"/> and
/// <see cref="TryIndex"/> rather than reading a pointer itself.</summary>
public static class DocumentJsonPointer {
    /// <summary>Escapes a member name as one pointer segment.</summary>
    /// <param name="value">The member name.</param>
    /// <returns>The segment, with <c>~</c> as <c>~0</c> and <c>/</c> as <c>~1</c>.</returns>
    public static string Escape(string value) => value.Replace(comparisonType: StringComparison.Ordinal, newValue: "~0", oldValue: "~")
        .Replace(comparisonType: StringComparison.Ordinal, newValue: "~1", oldValue: "/");
    /// <summary>Names a member of the node a pointer stands at.</summary>
    /// <param name="pointer">The pointer of the parent; empty for the document itself.</param>
    /// <param name="name">The unescaped member name.</param>
    /// <returns>The parent's pointer followed by <paramref name="name"/> as one escaped segment.</returns>
    public static string Child(string pointer, string name) => string.Concat(str0: pointer, str1: "/", str2: Escape(value: name));
    /// <summary>Names an element of the list a pointer stands at.</summary>
    /// <param name="pointer">The pointer of the list; empty for the document itself.</param>
    /// <param name="index">The zero-based element index.</param>
    /// <returns>The list's pointer followed by <paramref name="index"/> as one segment.</returns>
    public static string Child(string pointer, int index) => string.Concat(str0: pointer, str1: "/", str2: index.ToString(provider: CultureInfo.InvariantCulture));
    /// <summary>Reads a pointer segment back to the member name it escapes.</summary>
    /// <param name="value">The segment.</param>
    /// <returns>The member name.</returns>
    public static string Unescape(string value) => value.Replace(comparisonType: StringComparison.Ordinal, newValue: "/", oldValue: "~1")
        .Replace(comparisonType: StringComparison.Ordinal, newValue: "~", oldValue: "~0");
    /// <summary>Reads a pointer as its member names and array indexes.</summary>
    /// <param name="pointer">The pointer; empty names the document itself.</param>
    /// <param name="segments">The unescaped segments on success; empty for the document itself.</param>
    /// <returns><see langword="false"/> when <paramref name="pointer"/> is malformed: it does not start with
    /// <c>/</c>, or holds a <c>~</c> not followed by <c>0</c> or <c>1</c>.</returns>
    public static bool TrySegments(string pointer, out string[] segments) {
        segments = [];

        if (pointer.Length == 0) {
            return true;
        }
        if (pointer[0] != '/') {
            return false;
        }

        var parts = pointer[1..].Split(separator: '/');

        foreach (var part in parts) {
            for (var at = 0; (at < part.Length); at++) {
                if ((part[at] == '~') && ((++at == part.Length) || (part[at] is not ('0' or '1')))) {
                    return false;
                }
            }
        }
        segments = [.. parts.Select(selector: Unescape)];
        return true;
    }
    /// <summary>Reads a pointer as its member names and array indexes, refusing a malformed one.</summary>
    /// <param name="pointer">The pointer; empty names the document itself.</param>
    /// <returns>The unescaped segments; empty for the document itself.</returns>
    /// <exception cref="ArgumentException"><paramref name="pointer"/> is malformed.</exception>
    public static string[] Segments(string pointer) => (TrySegments(pointer: pointer, segments: out var segments) ? segments : throw Missing(pointer: pointer));
    /// <summary>Creates the refusal for a pointer that is malformed or that a document holds nothing at.</summary>
    /// <param name="pointer">The pointer.</param>
    /// <returns>The exception to throw.</returns>
    public static ArgumentException Missing(string pointer) => new(message: $"Invalid or missing JSON Pointer '{pointer}'.", paramName: nameof(pointer));
    /// <summary>Reads a segment as an index into a list.</summary>
    /// <param name="segment">The segment: digits only, with no leading zero (RFC 6901).</param>
    /// <param name="count">The list's length.</param>
    /// <param name="index">The index on success.</param>
    /// <returns>Whether <paramref name="segment"/> names an element of a list of <paramref name="count"/>.</returns>
    public static bool TryIndex(string segment, int count, out int index) {
        index = -1;
        return ((segment.Length > 0) && ((segment.Length == 1) || (segment[0] != '0')) &&
            int.TryParse(s: segment, style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out index) && (index < count));
    }
    /// <summary>Steps from a node to one of its children.</summary>
    /// <param name="node">An object or a list; any other node holds no children.</param>
    /// <param name="segment">The unescaped member name, or the index.</param>
    /// <param name="child">The child on success; <see langword="null"/> when the member holds JSON <c>null</c>.</param>
    /// <returns>Whether <paramref name="node"/> holds the member or the element.</returns>
    public static bool TryChild(JsonNode? node, string segment, out JsonNode? child) {
        switch (node) {
            case JsonObject obj:
                return obj.TryGetPropertyValue(jsonNode: out child, propertyName: segment);
            case JsonArray array when TryIndex(count: array.Count, index: out var index, segment: segment):
                child = array[index];
                return true;
            default:
                child = null;
                return false;
        }
    }
    /// <summary>Finds the node a path of segments names.</summary>
    /// <param name="root">The document.</param>
    /// <param name="segments">The unescaped segments, from <see cref="TrySegments"/>.</param>
    /// <param name="node">The node on success.</param>
    /// <returns>Whether the document holds every step of the path; a missing step is <see langword="false"/>, never
    /// a throw.</returns>
    public static bool TryAt(JsonNode? root, IEnumerable<string> segments, out JsonNode? node) {
        node = root;

        foreach (var segment in segments) {
            if (!TryChild(child: out node, node: node, segment: segment)) {
                return false;
            }
        }
        return true;
    }
    /// <summary>Finds the node a pointer names.</summary>
    /// <param name="root">The document.</param>
    /// <param name="pointer">The pointer; empty names <paramref name="root"/>.</param>
    /// <param name="node">The node on success; <see langword="null"/> when the member holds JSON <c>null</c>.</param>
    /// <returns>Whether the pointer is well formed and the document holds every step of it.</returns>
    public static bool TryAt(JsonNode? root, string pointer, out JsonNode? node) {
        if (TrySegments(pointer: pointer, segments: out var segments)) {
            return TryAt(node: out node, root: root, segments: segments);
        }

        node = null;
        return false;
    }
    /// <summary>Reports whether a document holds the member or element a pointer names.</summary>
    /// <param name="root">The document.</param>
    /// <param name="pointer">The pointer; empty names <paramref name="root"/>.</param>
    /// <returns>Whether the pointer is well formed and the document holds every step of it, a member holding JSON
    /// <c>null</c> included.</returns>
    public static bool Exists(JsonNode? root, string pointer) => TryAt(node: out _, pointer: pointer, root: root);
    /// <summary>Returns the node a pointer names.</summary>
    /// <param name="root">The document.</param>
    /// <param name="pointer">The pointer; empty names <paramref name="root"/>.</param>
    /// <returns>The node, or <see langword="null"/> when the document holds nothing there (a member holding JSON
    /// <c>null</c> reads the same; <see cref="TryAt(JsonNode?, string, out JsonNode?)"/> tells them apart).</returns>
    public static JsonNode? At(JsonNode? root, string pointer) => (TryAt(node: out var node, pointer: pointer, root: root) ? node : null);
    /// <summary>Returns the node a pointer names, refusing when the document holds none.</summary>
    /// <param name="root">The document.</param>
    /// <param name="pointer">The pointer; empty names <paramref name="root"/>.</param>
    /// <returns>The node; <see langword="null"/> when the member holds JSON <c>null</c>.</returns>
    /// <exception cref="ArgumentException">The pointer is malformed or the document holds no step of it.</exception>
    public static JsonNode? Require(JsonNode? root, string pointer) => (TryAt(node: out var node, pointer: pointer, root: root)
        ? node
        : throw Missing(pointer: pointer));
    /// <summary>Returns the pointer to the container of the node a pointer names.</summary>
    /// <param name="pointer">A pointer.</param>
    /// <returns>The pointer without its last segment; empty for a top-level member or the document itself.</returns>
    public static string Parent(string pointer) => pointer[..Math.Max(val1: 0, val2: pointer.LastIndexOf(value: '/'))];
    /// <summary>Returns the deepest node of a document that lies on the way to a pointer.</summary>
    /// <param name="root">The document.</param>
    /// <param name="pointer">The pointer.</param>
    /// <returns>The node the pointer names, else its nearest ancestor the document holds, else
    /// <see langword="null"/> when <paramref name="root"/> is.</returns>
    public static JsonNode? Nearest(JsonNode? root, string pointer) {
        for (var at = pointer; ; at = Parent(pointer: at)) {
            if (At(root: root, pointer: at) is { } node) {
                return node;
            }
            if (at.Length == 0) {
                return null;
            }
        }
    }
    /// <summary>Visits every scalar of a tree with the pointer that names it.</summary>
    /// <param name="node">The tree.</param>
    /// <param name="pointer">The pointer <paramref name="node"/> stands at; empty for a document.</param>
    /// <param name="visit">Called with each scalar and its pointer.</param>
    public static void WalkValues(JsonNode? node, string pointer, Action<JsonValue, string> visit) {
        switch (node) {
            case JsonValue value:
                visit(arg1: value, arg2: pointer);
                break;
            case JsonObject obj:
                foreach (var (name, child) in obj) {
                    WalkValues(node: child, pointer: Child(name: name, pointer: pointer), visit: visit);
                }
                break;
            case JsonArray array:
                for (var index = 0; (index < array.Count); index++) {
                    WalkValues(node: array[index], pointer: Child(index: index, pointer: pointer), visit: visit);
                }
                break;
        }
    }
}
