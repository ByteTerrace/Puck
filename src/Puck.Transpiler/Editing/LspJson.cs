using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Editing;

/// <summary>The one builder of the Language Server Protocol values Puck's editor services answer with: completion
/// items, positions and ranges, and the conversion between an LSP position and a source offset.</summary>
/// <remarks>LSP positions are 0-based lines and 0-based characters within a line, counted in UTF-16 code units, which
/// is how a .NET string counts; a line ends at <c>\n</c>.</remarks>
public static class LspJson {
    /// <summary>The <c>insertTextFormat</c> that marks an item's insert text as a snippet, whose <c>$1</c> and
    /// <c>${1:name}</c> are tab stops.</summary>
    public const int SnippetFormat = 2;

    /// <summary>Returns a completion item whose insert text is a snippet.</summary>
    /// <param name="label">The word the list shows.</param>
    /// <param name="insertText">The snippet the editor inserts.</param>
    /// <param name="detail">The line that says what the item is.</param>
    /// <param name="kind">The LSP <c>CompletionItemKind</c>.</param>
    /// <returns>The item.</returns>
    public static JsonObject Completion(string label, string insertText, string detail, int kind) => new() {
        ["label"] = label,
        ["kind"] = kind,
        ["detail"] = detail,
        ["insertText"] = insertText,
        ["insertTextFormat"] = SnippetFormat,
    };
    /// <summary>Appends a completion item whose insert text is a snippet (<see cref="Completion"/>).</summary>
    /// <param name="items">The completion list.</param>
    /// <param name="label">The word the list shows.</param>
    /// <param name="insertText">The snippet the editor inserts.</param>
    /// <param name="detail">The line that says what the item is.</param>
    /// <param name="kind">The LSP <c>CompletionItemKind</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    public static void AddCompletion(JsonArray items, string label, string insertText, string detail, int kind) {
        ArgumentNullException.ThrowIfNull(argument: items);

        items.Add(item: Completion(
            detail: detail,
            insertText: insertText,
            kind: kind,
            label: label
        ));
    }
    /// <summary>Returns an LSP position.</summary>
    /// <param name="line">The 0-based line.</param>
    /// <param name="character">The 0-based character within the line.</param>
    /// <returns>The position.</returns>
    public static JsonObject Position(int line, int character) => new() {
        ["line"] = line,
        ["character"] = character,
    };
    /// <summary>Returns an LSP range between two positions.</summary>
    /// <param name="start">The first position, as <see cref="PositionOf"/> returns one.</param>
    /// <param name="end">The position just past the last character.</param>
    /// <returns>The range.</returns>
    public static JsonObject Range((int Line, int Character) start, (int Line, int Character) end) => new() {
        ["start"] = Position(character: start.Character, line: start.Line),
        ["end"] = Position(character: end.Character, line: end.Line),
    };
    /// <summary>Returns the LSP range a source span covers. The start is the span's own line and column; the end is
    /// where the span's last character is followed, so a span that crosses line breaks ends on the line it ends on.</summary>
    /// <param name="span">The span, with its 1-based line and column.</param>
    /// <param name="source">The text the span was read from, or <see langword="null"/> when the span belongs to another
    /// text; without it, the end is counted along the start's line.</param>
    /// <returns>The range. An empty span covers one character, so an editor still has something to mark.</returns>
    public static JsonObject Range(SourceSpan span, string? source) {
        var start = (Math.Max(val1: 0, val2: (span.Line - 1)), Math.Max(val1: 0, val2: (span.Column - 1)));
        var end = (start.Item1, (start.Item2 + Math.Max(val1: 1, val2: span.Length)));

        if (
            (source is not null) &&
            (span.Length > 0) &&
            (span.Offset >= 0) &&
            (span.Offset <= (source.Length - span.Length))
        ) {
            var last = source.LastIndexOf(
                count: span.Length,
                startIndex: ((span.Offset + span.Length) - 1),
                value: '\n'
            );

            if (last >= 0) {
                end = ((start.Item1 + source.AsSpan(length: span.Length, start: span.Offset).Count(value: '\n')), ((span.Offset + span.Length) - (last + 1)));
            }
        }

        return Range(end: end, start: start);
    }
    /// <summary>Returns the source offset an LSP position names.</summary>
    /// <param name="source">The text.</param>
    /// <param name="line">The 0-based line.</param>
    /// <param name="character">The 0-based character within the line.</param>
    /// <returns>The offset of the line's start plus <paramref name="character"/>; a line past the text's last starts
    /// one past the text's end.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public static int Offset(string source, int line, int character) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var start = 0;

        for (var remaining = line; (remaining > 0); remaining--) {
            var next = source.IndexOf(startIndex: start, value: '\n');

            if (next < 0) {
                return ((source.Length + 1) + character);
            }
            start = (next + 1);
        }

        return (start + character);
    }
    /// <summary>Returns the LSP position of a source offset.</summary>
    /// <param name="source">The text.</param>
    /// <param name="offset">The 0-based offset, clamped to the text.</param>
    /// <returns>The 0-based line and character.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public static (int Line, int Character) PositionOf(string source, int offset) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var at = Math.Clamp(max: source.Length, min: 0, value: offset);
        var lineStart = ((at == 0) ? 0 : (source.LastIndexOf(startIndex: (at - 1), value: '\n') + 1));

        return (source.AsSpan(length: at, start: 0).Count(value: '\n'), (at - lineStart));
    }
}
