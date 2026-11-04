using System.Globalization;
using System.Text.Json.Nodes;

namespace Puck.World;

/// <summary>The one reading of an RFC 6901 JSON pointer into a document tree: the escaping of a member name, the node a
/// pointer names, and the deepest node that exists on the way to one that does not.</summary>
public static class WorldJsonPointer {
    /// <summary>Escapes a member name as one pointer segment.</summary>
    /// <param name="value">The member name.</param>
    /// <returns>The segment, with <c>~</c> as <c>~0</c> and <c>/</c> as <c>~1</c>.</returns>
    public static string Escape(string value) => value.Replace(comparisonType: StringComparison.Ordinal, newValue: "~0", oldValue: "~")
        .Replace(comparisonType: StringComparison.Ordinal, newValue: "~1", oldValue: "/");
    /// <summary>Reads a pointer segment back to the member name it escapes.</summary>
    /// <param name="value">The segment.</param>
    /// <returns>The member name.</returns>
    public static string Unescape(string value) => value.Replace(comparisonType: StringComparison.Ordinal, newValue: "/", oldValue: "~1")
        .Replace(comparisonType: StringComparison.Ordinal, newValue: "~", oldValue: "~0");
    /// <summary>Returns the node a pointer names.</summary>
    /// <param name="root">The document.</param>
    /// <param name="pointer">The pointer; empty names <paramref name="root"/>.</param>
    /// <returns>The node, or <see langword="null"/> when the document holds nothing there.</returns>
    public static JsonNode? At(JsonNode? root, string pointer) {
        foreach (var part in pointer.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '/')) {
            root = root switch {
                JsonObject obj => obj[Unescape(value: part)],
                JsonArray array when (int.TryParse(s: part, provider: CultureInfo.InvariantCulture, result: out var index) && (index >= 0) && (index < array.Count)) => array[index],
                _ => null,
            };
        }

        return root;
    }
    /// <summary>Returns the deepest node of a document that lies on the way to a pointer.</summary>
    /// <param name="root">The document.</param>
    /// <param name="pointer">The pointer.</param>
    /// <returns>The node the pointer names, else its nearest ancestor the document holds, else
    /// <see langword="null"/> when <paramref name="root"/> is.</returns>
    public static JsonNode? Nearest(JsonNode? root, string pointer) {
        for (var at = pointer; ; at = at[..at.LastIndexOf(value: '/')]) {
            if (At(root: root, pointer: at) is { } node) {
                return node;
            }
            if (at.Length == 0) {
                return null;
            }
        }
    }
}
