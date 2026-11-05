using Puck.Assets.Documents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.GamingBricks.Forge;

/// <summary>An editor-owned source document. Edits may be temporarily invalid; builds always validate.</summary>
public sealed class CartridgeDraft {
    private JsonNode? m_previous;
    private JsonNode m_root;

    /// <summary>Starts a draft from validated source data.</summary>
    /// <param name="document">The initial cartridge.</param>
    public CartridgeDraft(CartridgeDocument document) {
        m_root = JsonNode.Parse(utf8Json: CartridgeDocuments.Canonicalize(document: document).Bytes)!;
    }

    private static void CheckSize(string json) {
        if (Encoding.UTF8.GetByteCount(s: json) > CartridgeDocuments.MaximumSourceBytes) {
            throw new ArgumentException(
            message: "The draft exceeds the cartridge source size limit.",
            paramName: nameof(json)
        );
        }
    }
    private void Edit(string pointer, JsonNode? value, bool remove) {
        var parts = DocumentJsonPointer.Segments(pointer: pointer);
        var next = m_root.DeepClone();

        if (parts.Length == 0) {
            if (
                remove ||
                (value is not JsonObject)
            ) {
                throw new ArgumentException(
                message: "The draft root must remain an object.",
                paramName: nameof(pointer)
            );
            }
            next = value;
        } else {
            var parent = DocumentJsonPointer.Require(
                pointer: DocumentJsonPointer.Parent(pointer: pointer),
                root: next
            );
            var key = parts[^1];

            switch (parent) {
                case JsonObject obj:
                    if (remove) {
                        if (!obj.Remove(propertyName: key)) { throw DocumentJsonPointer.Missing(pointer: pointer); }
                    } else { obj[propertyName: key] = value; }
                    break;
                case JsonArray array:
                    if (
                        !remove &&
                        (key == "-")
                    ) { array.Add(item: value); break; }
                    if (!DocumentJsonPointer.TryIndex(count: array.Count, index: out var index, segment: key)) { throw DocumentJsonPointer.Missing(pointer: pointer); }
                    if (remove) { array.RemoveAt(index: index); } else { array[index: index] = value; }
                    break;
                default: throw DocumentJsonPointer.Missing(pointer: pointer);
            }
        }
        CheckSize(json: next.ToJsonString());
        m_previous = m_root;
        m_root = next;
    }
    /// <summary>Validates the draft and returns canonical source data.</summary>
    /// <returns>The valid cartridge document.</returns>
    public CartridgeDocument Check() => CartridgeDocuments.Parse(utf8: Encoding.UTF8.GetBytes(s: Show()));
    /// <summary>Removes an existing property or array element atomically.</summary>
    /// <param name="pointer">The existing JSON Pointer.</param>
    public void Remove(string pointer) => Edit(
        pointer: pointer,
        remove: true,
        value: null
    );
    /// <summary>Sets a property or array element; a final '-' appends to an array. Failure leaves the draft intact.</summary>
    /// <param name="pointer">The destination JSON Pointer.</param>
    /// <param name="json">The new JSON value.</param>
    public void Set(string pointer, string json) {
        CheckSize(json: json);
        var value = JsonNode.Parse(
            json: json,
            documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 32 }
        );

        Edit(
            pointer: pointer,
            remove: false,
            value: value
        );
    }
    /// <summary>Reads the entire draft or a JSON Pointer subtree as JSON.</summary>
    /// <param name="pointer">An RFC 6901 pointer; empty selects the root.</param>
    /// <returns>The selected JSON, including string quotes.</returns>
    public string Show(string pointer = "") => (DocumentJsonPointer.Require(pointer: pointer, root: m_root)?.ToJsonString() ?? "null");
    /// <summary>Swaps the current draft with the last successful edit, allowing undo and redo.</summary>
    public void Undo() {
        if (m_previous is null) { throw new InvalidOperationException(message: "No edit to undo."); }
        (m_root, m_previous) = (m_previous, m_root);
    }
}
