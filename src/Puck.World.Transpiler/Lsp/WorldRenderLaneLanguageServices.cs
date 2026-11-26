using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.Transpiler.Editing;

namespace Puck.World.Transpiler.Lsp;

/// <summary>The language server's completion and hover for the render-lane names a creation writes
/// (<see cref="WorldRenderLaneNames"/>).</summary>
/// <remarks>A render-lane member is recognized by its name alone, read from the model
/// (<see cref="WorldRenderLaneNames.IsMember"/>), and by the position of its value on a line, so completion also
/// works in a buffer that does not parse yet. The names themselves are the composed document's, never this file's
/// alone, because an import declares lanes its importer writes.</remarks>
public static partial class WorldRenderLaneLanguageServices {
    // LSP CompletionItemKind: 20 EnumMember.
    private const int EnumMemberKind = 20;

    // `member: "typed` up to a cursor, or `member: "value"` around one. The member must stand alone as a word, so a
    // dotted or generated name never reads as one.
    [GeneratedRegex(pattern: "(?<![\\w$.])(?<member>[A-Za-z_]\\w*)[ \\t]*:[ \\t]*(?<open>\"?)(?<value>[A-Za-z0-9_\\-]*)(?<close>\"?)", options: RegexOptions.CultureInvariant)]
    private static partial Regex ValuePattern();

    /// <summary>Finds the render-lane member whose value the cursor is writing or standing in.</summary>
    /// <param name="text">The whole buffer.</param>
    /// <param name="offset">The cursor's 0-based offset into <paramref name="text"/>.</param>
    /// <param name="completing">Whether the cursor must stand on the value's characters (completion) or may also stand on
    /// its quotes (hover).</param>
    /// <param name="member">The render-lane member's name.</param>
    /// <param name="value">The value as written so far, without its quotes.</param>
    /// <param name="quoted">Whether the value is opened by a quote.</param>
    /// <returns><see langword="true"/> when the cursor is at a render-lane member's value.</returns>
    public static bool TryLocate(string text, int offset, bool completing, out string member, out string value, out bool quoted) {
        ArgumentNullException.ThrowIfNull(text);

        member = string.Empty;
        value = string.Empty;
        quoted = false;

        var at = Math.Clamp(max: text.Length, min: 0, value: offset);
        var lineStart = ((at == 0) ? 0 : (text.LastIndexOf(startIndex: (at - 1), value: '\n') + 1));
        var lineEnd = text.IndexOf(startIndex: at, value: '\n');
        var line = text[lineStart..((lineEnd < 0) ? text.Length : lineEnd)];
        var column = (at - lineStart);

        foreach (var match in ValuePattern().Matches(input: line).Cast<Match>()) {
            var name = match.Groups["member"].Value;
            var written = match.Groups["value"];
            var close = match.Groups["close"];

            if (!WorldRenderLaneNames.IsMember(name: name)) {
                continue;
            }
            // Completing stands on the value's characters, where an editor's closing quote may already follow the
            // cursor; hovering also stands on its quotes.
            var inside = (completing
                ? ((column >= written.Index) && (column <= (written.Index + written.Length)))
                : ((column >= match.Groups["open"].Index) && (column <= (close.Index + close.Length)))
            );

            if (!inside) {
                continue;
            }
            member = name;
            value = written.Value;
            quoted = (match.Groups["open"].Length > 0);

            return true;
        }

        return false;
    }

    /// <summary>Builds one completion per lane the composed document declares.</summary>
    /// <param name="lanes">The composed document's lanes in declared order (<see cref="WorldRenderLaneNames.Lanes"/>).</param>
    /// <param name="member">The render-lane member being written.</param>
    /// <param name="quoted">Whether the author already opened a quote, so the name is inserted without its own.</param>
    /// <returns>The completion items, in lane order.</returns>
    public static JsonArray Completions(IReadOnlyList<(string Name, int Lane)> lanes, string member, bool quoted) {
        ArgumentNullException.ThrowIfNull(lanes);
        ArgumentNullException.ThrowIfNull(member);

        var items = new JsonArray();

        foreach (var (name, lane) in lanes) {
            items.Add(item: LspJson.Completion(
                detail: $"render lane {lane}: `{member}` reads this lane of the world",
                insertText: (quoted ? name : $"\"{name}\""),
                kind: EnumMemberKind,
                label: name
            ));
        }

        return items;
    }

    /// <summary>Returns the hover card for a lane name written at a render-lane member.</summary>
    /// <param name="lanes">The composed document's lanes in declared order.</param>
    /// <param name="member">The render-lane member the name is written at.</param>
    /// <param name="value">The name under the cursor.</param>
    /// <returns>The card as Markdown naming the lane and its index, or <see langword="null"/> when the value is a lane
    /// index or empty, or when no lane is known to compare it with.</returns>
    public static string? Hover(IReadOnlyList<(string Name, int Lane)> lanes, string member, string value) {
        ArgumentNullException.ThrowIfNull(lanes);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(value);

        if ((value.Length == 0) || char.IsAsciiDigit(c: value[0])) {
            return null;
        }
        foreach (var (name, lane) in lanes) {
            if (string.Equals(a: name, b: value, comparisonType: StringComparison.Ordinal)) {
                return $"**render lane `{name}`**\n\nRender lane {lane}, declared by the composed document's `render.lanes`. `{member}` reads it by this name; the load writes the index.";
            }
        }

        return ((lanes.Count == 0)
            ? null
            : $"**`{value}`** names no render lane; the composed document's `render.lanes` declares {string.Join(separator: ", ", values: lanes.Select(selector: static lane => $"`{lane.Name}`"))}."
        );
    }
}
