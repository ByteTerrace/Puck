using System.Text.Json.Nodes;

namespace Puck.World;

/// <summary>The names a world document's author declares, read off the document's raw tree: every declaration the
/// <see cref="WorldNameRegistry"/> lists, every row's <c>id</c>, every declared cell's <c>key</c>, and the <c>name</c>
/// of every <c>references</c> and <c>destinations</c> row. The root <c>extensions</c> bag is the author's own free data
/// and is not read.
/// <para>Both doors that hold an author's names to <see cref="GeneratedName"/> read this one walk: the compiler,
/// which refuses either reserved spelling where the author wrote it, and the loader
/// (<see cref="WorldDefinitionFileSource.TryParseDocument"/>), which refuses <see cref="GeneratedName.FileJoiner"/> in
/// every document it parses (<see cref="TryRefuseFileJoiner"/>).</para></summary>
public static class WorldAuthoredNames {
    private static readonly string[] NamedSections = ["destinations", "references"];

    /// <summary>Calls <paramref name="visitor"/> once for every string leaf of <paramref name="document"/> that holds
    /// an authored name, in document order: the registry's declarations first, then the <c>id</c>, cell <c>key</c>, and
    /// reference and destination <c>name</c> members.</summary>
    /// <param name="document">The document's raw tree; not modified.</param>
    /// <param name="visitor">Called with each name's node and text.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="visitor"/> is
    /// <see langword="null"/>.</exception>
    public static void Visit(JsonObject document, Action<JsonValue, string> visitor) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: visitor);

        var seen = new HashSet<JsonNode>(comparer: ReferenceEqualityComparer.Instance);

        void Check(JsonNode? node) {
            if (
                (node is JsonValue leaf) &&
                seen.Add(item: leaf) &&
                leaf.TryGetValue<string>(value: out var name)
            ) {
                visitor(
                    arg1: leaf,
                    arg2: name
                );
            }
        }
        void Walk(JsonNode? node) {
            switch (node) {
                case JsonObject obj:
                    foreach (var (member, child) in obj) {
                        if ((member == "id") && (child is JsonValue)) {
                            Check(node: child);
                        } else if ((member == "cells") && (child is JsonArray cells)) {
                            foreach (var cell in cells.OfType<JsonObject>()) {
                                Check(node: cell["key"]);
                            }
                        }

                        Walk(node: child);
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array) {
                        Walk(node: item);
                    }

                    break;
                default:
                    break;
            }
        }

        WorldModuleNamespace.Visit(
            node: document,
            type: typeof(WorldDefinition),
            visitor: (_, _, value, field, _) => {
                if (field.Role == WorldNameRole.Declares) {
                    Check(node: value);
                }
            }
        );

        foreach (var (member, child) in document) {
            if (member == "extensions") {
                continue;
            }
            if (
                (Array.IndexOf(array: NamedSections, value: member) >= 0) &&
                (child is JsonArray rows)
            ) {
                foreach (var row in rows.OfType<JsonObject>()) {
                    Check(node: row["name"]);
                }
            }

            Walk(node: child);
        }
    }
    /// <summary>Refuses a document that holds an authored name carrying <see cref="GeneratedName.FileJoiner"/>: the
    /// compiler never writes that character into a document name, so a name carrying it was written by an author,
    /// and a destination carrying it could not be spelled as the directory its instance starts in
    /// (<see cref="GeneratedName.ToFile"/>).</summary>
    /// <param name="document">The document's raw tree; not modified.</param>
    /// <param name="reason">The refusal, naming the first such name and where it stands, or empty on success.</param>
    /// <returns><see langword="true"/> when no authored name carries the character.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static bool TryRefuseFileJoiner(JsonObject document, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var refusal = string.Empty;

        Visit(
            document: document,
            visitor: (node, name) => {
                if (
                    (refusal.Length == 0) &&
                    !GeneratedName.TryValidateDocument(
                    name: name,
                    reason: out var why
                )
                ) {
                    refusal = $"{node.GetPath()}: {why}";
                }
            }
        );

        reason = refusal;

        return (refusal.Length == 0);
    }
}
