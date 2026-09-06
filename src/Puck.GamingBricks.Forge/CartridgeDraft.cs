using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.GamingBricks.Forge;

/// <summary>An editor-owned source document. Edits may be temporarily invalid; builds always validate.</summary>
public sealed class CartridgeDraft {
    private JsonNode m_root;
    private JsonNode? m_previous;

    /// <summary>Starts a draft from validated source data.</summary>
    /// <param name="document">The initial cartridge.</param>
    public CartridgeDraft(CartridgeDocument document) {
        m_root = JsonNode.Parse(utf8Json: CartridgeDocuments.Canonicalize(document: document).Bytes)!;
    }

    /// <summary>Reads the entire draft or a JSON Pointer subtree as JSON.</summary>
    /// <param name="pointer">An RFC 6901 pointer; empty selects the root.</param>
    /// <returns>The selected JSON, including string quotes.</returns>
    public string Show(string pointer = "") => Resolve(root: m_root, parts: Parts(pointer: pointer))?.ToJsonString() ?? "null";

    /// <summary>Sets a property or array element; a final '-' appends to an array. Failure leaves the draft intact.</summary>
    /// <param name="pointer">The destination JSON Pointer.</param>
    /// <param name="json">The new JSON value.</param>
    public void Set(string pointer, string json) {
        CheckSize(json: json);
        var value = JsonNode.Parse(json: json, documentOptions: new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false });
        Edit(pointer: pointer, value: value, remove: false);
    }

    /// <summary>Removes an existing property or array element atomically.</summary>
    /// <param name="pointer">The existing JSON Pointer.</param>
    public void Remove(string pointer) => Edit(pointer: pointer, value: null, remove: true);

    /// <summary>Swaps the current draft with the last successful edit, allowing undo and redo.</summary>
    public void Undo() {
        if (m_previous is null) { throw new InvalidOperationException(message: "No edit to undo."); }
        (m_root, m_previous) = (m_previous, m_root);
    }

    /// <summary>Validates the draft and returns canonical source data.</summary>
    /// <returns>The valid cartridge document.</returns>
    public CartridgeDocument Check() => CartridgeDocuments.Parse(utf8: Encoding.UTF8.GetBytes(s: Show()));

    private void Edit(string pointer, JsonNode? value, bool remove) {
        var parts = Parts(pointer: pointer);
        var next = m_root.DeepClone();
        if (parts.Length == 0) {
            if (remove || value is not JsonObject) { throw new ArgumentException(message: "The draft root must remain an object.", paramName: nameof(pointer)); }
            next = value;
        } else {
            var parent = Resolve(root: next, parts: parts[..^1]);
            var key = parts[^1];
            switch (parent) {
                case JsonObject obj:
                    if (remove) {
                        if (!obj.Remove(propertyName: key)) { throw Missing(pointer: pointer); }
                    } else { obj[propertyName: key] = value; }
                    break;
                case JsonArray array:
                    if (!remove && key == "-") { array.Add(item: value); break; }
                    var index = Index(key: key, count: array.Count);
                    if (remove) { array.RemoveAt(index: index); } else { array[index: index] = value; }
                    break;
                default: throw Missing(pointer: pointer);
            }
        }
        CheckSize(json: next.ToJsonString());
        m_previous = m_root;
        m_root = next;
    }

    private static JsonNode? Resolve(JsonNode root, string[] parts) {
        var node = root;
        foreach (var part in parts) {
            node = node switch {
                JsonObject obj when obj.ContainsKey(propertyName: part) => obj[propertyName: part],
                JsonArray array => array[index: Index(key: part, count: array.Count)],
                _ => throw Missing(pointer: part),
            };
        }
        return node;
    }

    private static string[] Parts(string pointer) {
        if (pointer.Length == 0) { return []; }
        if (!pointer.StartsWith(value: '/')) { throw Missing(pointer: pointer); }
        var parts = pointer[1..].Split(separator: '/');
        foreach (var part in parts) {
            for (var i = 0; i < part.Length; ++i) {
                if (part[i] == '~' && (++i == part.Length || part[i] is not ('0' or '1'))) { throw Missing(pointer: pointer); }
            }
        }
        return parts.Select(selector: static part => part.Replace(oldValue: "~1", newValue: "/", comparisonType: StringComparison.Ordinal).Replace(oldValue: "~0", newValue: "~", comparisonType: StringComparison.Ordinal)).ToArray();
    }

    private static int Index(string key, int count) {
        if (key.Length == 0 || (key.Length > 1 && key[0] == '0') || !int.TryParse(s: key, style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out var index) || index < 0 || index >= count) { throw Missing(pointer: key); }
        return index;
    }
    private static ArgumentException Missing(string pointer) => new(message: $"Invalid or missing JSON Pointer '{pointer}'.");
    private static void CheckSize(string json) {
        if (Encoding.UTF8.GetByteCount(s: json) > CartridgeDocuments.MaximumSourceBytes) { throw new ArgumentException(message: "The draft exceeds the cartridge source size limit.", paramName: nameof(json)); }
    }
}
