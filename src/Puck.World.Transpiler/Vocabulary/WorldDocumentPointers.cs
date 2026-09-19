using System.Text;

namespace Puck.World.Transpiler.Vocabulary;

/// <summary>The one spelling of a document path as the JSON pointer the lowering registered for it.</summary>
/// <remarks>A refusal names the field it refuses in dotted-and-bracketed form (<c>placements[0].prototypeId</c>),
/// which is the document's own shape only where a section's rows are the section itself. Where the description says
/// a section's rows live under a named member — a <c>placement</c> lowers to <c>placements.rows[]</c> — the index
/// belongs after that member, and a pointer that skips it resolves to the section's span instead of the row's
/// own.</remarks>
public static class WorldDocumentPointers {
    private readonly record struct PathToken(string Text, bool IsIndex);

    private static bool Describes(WorldConstructTable table, string documentMember) => table.Constructs.Any(predicate: construct => construct.DocumentMembers.Contains(
        comparer: StringComparer.Ordinal,
        value: documentMember
    ));
    // The one member a described construct puts the rows of `prefix` under, or null when the description names
    // none or names several.
    private static string? RowsMemberOf(WorldConstructTable table, string prefix) {
        var head = $"{prefix}.";
        var candidates = table.Constructs
            .SelectMany(selector: static construct => construct.DocumentMembers)
            .Where(predicate: member => (
            member.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: head
        ) &&
            member.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "[]"
        )
        ))
            .Select(selector: member => member[head.Length..^2])
            .Where(predicate: static tail => !tail.Contains(
            comparisonType: StringComparison.Ordinal,
            value: "."
        ))
            .Distinct(comparer: StringComparer.Ordinal)
            .ToArray();

        return ((candidates.Length == 1)
            ? candidates[0]
            : null
        );
    }
    private static IEnumerable<PathToken> Tokens(string path) {
        var text = new StringBuilder();

        for (var index = 0; (index < path.Length); index++) {
            var character = path[index];

            if (character == '.') {
                if (text.Length > 0) {
                    yield return new PathToken(
                        IsIndex: false,
                        Text: text.ToString()
                    );
                    _ = text.Clear();
                }

                continue;
            }
            if (character == '[') {
                if (text.Length > 0) {
                    yield return new PathToken(
                        IsIndex: false,
                        Text: text.ToString()
                    );
                    _ = text.Clear();
                }
                while ((++index < path.Length) && (path[index] != ']')) {
                    _ = text.Append(value: path[index]);
                }

                yield return new PathToken(
                    IsIndex: true,
                    Text: text.ToString()
                );
                _ = text.Clear();

                continue;
            }
            _ = text.Append(value: character);
        }
        if (text.Length > 0) {
            yield return new PathToken(
                IsIndex: false,
                Text: text.ToString()
            );
        }
    }

    /// <summary>Returns the JSON pointer a dotted-and-bracketed document path names.</summary>
    /// <param name="path">The path as a refusal spells it; empty for the document's own root.</param>
    /// <param name="table">The described vocabulary, which says where a section's rows live.</param>
    /// <returns>The pointer with its leading slash, or the empty string for an empty path.</returns>
    public static string ToJsonPointer(string path, WorldConstructTable table) {
        ArgumentNullException.ThrowIfNull(table);

        if (string.IsNullOrEmpty(value: path)) {
            return "";
        }

        var member = new StringBuilder();
        var pointer = new StringBuilder();

        foreach (var token in Tokens(path: path)) {
            if (!token.IsIndex) {
                if (member.Length > 0) {
                    _ = member.Append(value: '.');
                }
                _ = member.Append(value: token.Text);
                _ = pointer.Append(value: '/').Append(value: token.Text);

                continue;
            }
            if (
                !Describes(
                documentMember: $"{member}[]",
                table: table
            ) &&
                (RowsMemberOf(
                prefix: member.ToString(),
                table: table
            ) is { } rows)
            ) {
                _ = member.Append(value: '.').Append(value: rows);
                _ = pointer.Append(value: '/').Append(value: rows);
            }
            _ = member.Append(value: "[]");
            _ = pointer.Append(value: '/').Append(value: token.Text);
        }

        return pointer.ToString();
    }
}
