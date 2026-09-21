using System.Text.Json.Nodes;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler.Lsp;

/// <summary>The language server's construct completions and hover cards, taken from a construct table rather than
/// from a list of their own.</summary>
/// <remarks>Every entry reads the table it is handed, so a construct or member added to a description is offered
/// and explained without an edit here.</remarks>
public static class WorldConstructLanguageServices {
    // LSP CompletionItemKind: 3 Function, 5 Field, 7 Class, 14 Keyword.
    private const int FieldKind = 5;
    private const int FunctionKind = 3;
    private const int KeywordKind = 14;
    private const int SectionKind = 7;

    private static int KindOf(WorldConstructShape shape) => (shape is WorldConstructShape.Section or WorldConstructShape.Row
        ? SectionKind
        : KeywordKind
    );
    private static int KindOf(WorldMemberPosition position) => (position switch {
        WorldMemberPosition.Modifier => FunctionKind,
        _ => FieldKind,
    });
    private static string InsertTextFor(WorldConstructMember member) => member.Position switch {
        WorldMemberPosition.Modifier => $"{member.Name}(${{1:}})",
        WorldMemberPosition.Property => $"{member.Name}: ${{1:}}",
        _ => member.Name,
    };
    private static JsonObject Item(string label, string insertText, string detail, int kind) => new() {
        ["detail"] = detail,
        ["insertText"] = insertText,
        ["insertTextFormat"] = 2,
        ["kind"] = kind,
        ["label"] = label,
    };

    /// <summary>Returns the keyword of the construct whose statement the cursor sits in.</summary>
    /// <param name="offset">The cursor's 0-based offset into <paramref name="text"/>.</param>
    /// <param name="table">The description keywords are recognized against.</param>
    /// <param name="text">The whole document as authored.</param>
    /// <returns>The enclosing construct's keyword, or <see langword="null"/> at the document's own root or inside
    /// a construct the table does not describe.</returns>
    /// <remarks>The cursor's own line decides first, since a declaration writes its header members on the keyword's
    /// line (<c>grid board dimensions(…)</c>); otherwise the innermost block still open at the cursor does.
    /// String literals and comments are skipped, so a brace inside either does not open a block.</remarks>
    public static string? ConstructAt(WorldConstructTable table, string text, int offset) {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(text);

        var limit = Math.Clamp(
            max: text.Length,
            min: 0,
            value: offset
        );
        var open = new Stack<string?>();
        var lineWord = (string?)null;
        var lineWordEnd = 0;
        var lineStart = 0;
        var index = 0;

        while (index < text.Length) {
            var character = text[index];

            if (character == '\n') {
                if (index >= limit) {
                    break;
                }
                lineWord = null;
                lineStart = (index + 1);
                index++;

                continue;
            }
            if (
                (character == '/') &&
                ((index + 1) < text.Length) &&
                (text[index + 1] is '/' or '*')
            ) {
                var close = ((text[index + 1] == '/')
                    ? text.IndexOf(startIndex: index, value: '\n')
                    : text.IndexOf(
                        comparisonType: StringComparison.Ordinal,
                        startIndex: index,
                        value: "*/"
                    )
                );

                index = ((close < 0)
                    ? text.Length
                    : ((text[index + 1] == '/')
                        ? close
                        : (close + 2))
                );

                continue;
            }
            if (character == '"') {
                index++;
                while (index < text.Length) {
                    if (text[index] == '\\') {
                        index += 2;

                        continue;
                    }
                    if (text[index] is '"' or '\n') {
                        break;
                    }
                    index++;
                }
                index++;

                continue;
            }
            if (char.IsLetter(c: character) || (character == '_')) {
                var start = index;

                while ((index < text.Length) && (char.IsLetterOrDigit(c: text[index]) || (text[index] == '_'))) {
                    index++;
                }
                if (
                    (lineWord is null) &&
                    (text[lineStart..start].Trim().Length == 0)
                ) {
                    lineWord = text[start..index];
                    lineWordEnd = index;
                }

                continue;
            }
            if (character is '{' or '[') {
                open.Push(item: lineWord);
                index++;

                continue;
            }
            if (
                (character is '}' or ']') &&
                (open.Count > 0)
            ) {
                _ = open.Pop();
                index++;

                continue;
            }
            index++;
        }
        if (
            (lineWord is not null) &&
            (limit > lineWordEnd) &&
            table.TryGet(
            construct: out _,
            keyword: lineWord
        )
        ) {
            return lineWord;
        }

        foreach (var candidate in open) {
            if (
                (candidate is not null) &&
                table.TryGet(
                construct: out _,
                keyword: candidate
            )
            ) {
                return candidate;
            }
        }

        return null;
    }
    /// <summary>Appends one completion per construct the cursor's position admits, and one per member of the
    /// construct it sits in, to <paramref name="items"/>.</summary>
    /// <param name="enclosing">The keyword of the construct the cursor sits in, or <see langword="null"/> for the
    /// whole vocabulary.</param>
    /// <param name="items">The completion list being built.</param>
    /// <param name="table">The description every entry is taken from.</param>
    /// <remarks>With an enclosing construct, only what is legal there is offered: the constructs its body admits
    /// and its own members. With none, the whole table is, which is what a caller with no cursor context asks
    /// for.</remarks>
    public static void AddCompletions(JsonArray items, WorldConstructTable table, string? enclosing = null) {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(table);

        var offered = new HashSet<string>(comparer: StringComparer.Ordinal);
        var scoped = ((enclosing is not null) && table.TryGet(
            construct: out _,
            keyword: enclosing
        ));
        var constructs = (scoped
            ? table.Inside(enclosing: enclosing)
            : table.Constructs
        );

        foreach (var construct in constructs) {
            if (!offered.Add(item: construct.Keyword)) {
                continue;
            }
            items.Add(item: ((JsonNode?)Item(
                detail: $"{(construct.Enclosing is null
                    ? "Document"
                    : $"`{construct.Enclosing}`")} construct: lowers to {construct.DocumentMember}",
                insertText: (construct.Snippet ?? construct.Keyword),
                kind: KindOf(shape: construct.Shape),
                label: construct.Keyword
            )));
        }

        var described = (scoped
            ? table.Constructs.Where(predicate: candidate => string.Equals(
                a: candidate.Keyword,
                b: enclosing,
                comparisonType: StringComparison.Ordinal
            ))
            : table.Constructs
        );

        foreach (var construct in described) {
            foreach (var member in construct.Members) {
                if (
                    (member.Position is WorldMemberPosition.Body or WorldMemberPosition.Header) ||
                    !offered.Add(item: member.Name)
                ) {
                    continue;
                }
                items.Add(item: ((JsonNode?)Item(
                    detail: $"`{construct.Keyword}` member: {member.Summary}",
                    insertText: InsertTextFor(member: member),
                    kind: KindOf(position: member.Position),
                    label: member.Name
                )));
            }
        }
    }
    /// <summary>Returns the hover card for a word that names a described construct or one of its members.</summary>
    /// <param name="enclosing">The keyword of the construct the cursor sits in, or <see langword="null"/> when the
    /// caller has no context.</param>
    /// <param name="table">The description the card is taken from.</param>
    /// <param name="word">The word under the cursor.</param>
    /// <returns>The card as Markdown, or <see langword="null"/> when the table describes no such word.</returns>
    public static string? Hover(WorldConstructTable table, string word, string? enclosing = null) {
        ArgumentNullException.ThrowIfNull(table);

        return (table.TryDescribe(
            card: out var card,
            enclosing: enclosing,
            word: word
        )
            ? card
            : null
        );
    }
}
