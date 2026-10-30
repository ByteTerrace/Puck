using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Puck.Hosting;

namespace Puck.Mcp;

/// <summary>Narrows what a console answer returns to the caller, never what the command did: selected lines, lines
/// containing a text, selected fields, and a byte cap that ends in an explicit truncation mark. An answer that is JSON,
/// bare or in a <c>[verb: ...]</c> frame, is narrowed as data: the filters select the items of its arrays and keep the
/// listed properties. It applies to each line answer of <c>puck_exec</c>; a refused answer is returned whole.</summary>
internal sealed class ConsoleOutputFilter {
    /// <summary>The smallest <c>maxBytes</c> that leaves room for the truncation mark.</summary>
    internal const int MinimumBytes = 256;

    private readonly string[]? m_fields;
    private readonly int? m_maxBytes;
    private readonly string? m_match;
    private readonly (int First, int Last)[]? m_ranges;

    private ConsoleOutputFilter(string[]? fields, int? maxBytes, string? match, (int First, int Last)[]? ranges) {
        m_fields = fields;
        m_maxBytes = maxBytes;
        m_match = match;
        m_ranges = ranges;
    }

    /// <summary>Gets whether any selection or cap is set.</summary>
    internal bool Active => ((m_fields is not null) || (m_maxBytes is not null) || (m_match is not null) || (m_ranges is not null));

    /// <summary>Builds a filter from a call's arguments.</summary>
    /// <param name="fields">The field names to keep: keys of a <c>key=value</c> line, or property names anywhere in JSON output; <see langword="null"/> keeps every field.</param>
    /// <param name="lines">The one-based line ranges to keep, such as <c>1-20,40,60-</c>; <see langword="null"/> keeps every line.</param>
    /// <param name="match">The text a kept line contains, ignoring case; <see langword="null"/> keeps every line.</param>
    /// <param name="maxBytes">The most UTF-8 bytes each answer keeps; <see langword="null"/> applies no cap.</param>
    /// <param name="refusal">The name of the invalid argument, or <see langword="null"/> when the filter is valid.</param>
    /// <returns>The filter, or <see langword="null"/> when an argument is invalid.</returns>
    internal static ConsoleOutputFilter? Create(string[]? fields, string? lines, string? match, int? maxBytes, out string? refusal) {
        refusal = null;
        (int First, int Last)[]? ranges = null;

        if (fields is { Length: 0 } || (fields?.Any(predicate: static field => string.IsNullOrWhiteSpace(value: field)) == true)) { refusal = "fields"; return null; }
        if (match is { Length: 0 }) { refusal = "match"; return null; }
        if (maxBytes is < MinimumBytes) { refusal = "maxBytes"; return null; }
        if (lines is not null) {
            ranges = ParseRanges(lines: lines);
            if (ranges is null) { refusal = "lines"; return null; }
        }

        return new(fields: fields, maxBytes: maxBytes, match: match, ranges: ranges);
    }

    /// <summary>Applies the filter to a console result: its output, or each line answer of a script. A refused answer is
    /// kept whole.</summary>
    /// <param name="response">The host's bounded result.</param>
    /// <returns>The result with narrowed answers, its status and flags kept.</returns>
    internal ControlResponse Apply(ControlResponse response) {
        if (response.Commands is { } commands) {
            return (response with {
                Commands = [.. commands.Select(selector: line => {
                    if (line.IsError) { return line; }
                    var (output, truncated) = Narrow(output: line.Output, truncated: line.Truncated);

                    return (line with { Output = output, Truncated = truncated });
                })],
            });
        }
        if (response.IsError) { return response; }

        var (text, cut) = Narrow(output: response.Output, truncated: response.Truncated);

        return (response with { Output = text, Truncated = cut });
    }

    private (string Output, bool Truncated) Narrow(string output, bool truncated) {
        var text = output;

        if ((m_fields is not null) || (m_ranges is not null) || (m_match is not null)) {
            text = (!truncated && NarrowJson(output: output) is { } narrowed) ? narrowed : NarrowText(output: output);
        }
        if ((m_maxBytes is { } cap) && (Encoding.UTF8.GetByteCount(s: text) > cap)) { return (Cut(text: text, cap: cap), true); }

        return (text, truncated);
    }

    private static (int First, int Last)[]? ParseRanges(string lines) {
        var ranges = new List<(int, int)>();

        foreach (var part in lines.Split(separator: ',', options: StringSplitOptions.TrimEntries)) {
            var dash = part.IndexOf(value: '-');
            var head = ((dash < 0) ? part : part[..dash]);
            var tail = ((dash < 0) ? part : part[(dash + 1)..]);
            var first = 1;
            var last = int.MaxValue;

            if (((head.Length > 0) || (dash < 0)) && !(int.TryParse(s: head, style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out first) && (first >= 1))) { return null; }
            if ((tail.Length > 0) && !(int.TryParse(s: tail, style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out last) && (last >= first))) { return null; }
            if ((head.Length == 0) && (tail.Length == 0)) { return null; }
            ranges.Add((first, last));
        }

        return ((ranges.Count == 0) ? null : [.. ranges]);
    }
    private string NarrowText(string output) {
        var text = output;

        if ((m_ranges is not null) || (m_match is not null)) { text = KeepLines(text: text); }
        if (m_fields is not null) {
            text = KeepFields(text: text, fields: m_fields);
            if (text.Length == 0) { text = "[puck_exec: no field matched]"; }
        }

        return text;
    }
    // An answer that is JSON, bare or inside a "[verb: ...]" frame, narrowed as data: the items of each array are
    // selected by position and text, then the listed properties are kept. Null when the answer is not JSON.
    private string? NarrowJson(string output) {
        var trimmed = output.Trim();
        var prefix = string.Empty;
        var suffix = string.Empty;

        if (trimmed is ['[', ..] && trimmed.EndsWith(value: ']') && (trimmed.IndexOf(value: ": ", comparisonType: StringComparison.Ordinal) is var colon and > 1) && (trimmed.IndexOf(value: ' ') == (colon + 1))) {
            prefix = trimmed[..(colon + 2)];
            suffix = "]";
            trimmed = trimmed[prefix.Length..^1];
        }
        if (trimmed.AsSpan().TrimStart() is not [('{' or '['), ..]) { return null; }

        JsonNode? node;

        try { node = JsonNode.Parse(json: trimmed); } catch (JsonException) { return null; }
        var dropped = 0;

        if ((m_ranges is not null) || (m_match is not null)) { node = Select(node: node, dropped: ref dropped); }
        if (m_fields is not null) { node = Project(fields: m_fields, node: node); }

        var body = ((node is null) ? "[puck_exec: nothing selected]" : (prefix + node.ToJsonString() + suffix));

        return ((dropped > 0) ? $"{body}\n[puck_exec: {dropped} items dropped]" : body);
    }
    // Keeps the array items at the root, or in each array property of a root object, that sit in a listed range and
    // contain the match text.
    private JsonNode? Select(JsonNode? node, ref int dropped) {
        switch (node) {
            case JsonArray array: return SelectItems(array: array, dropped: ref dropped);
            case JsonObject source: {
                var kept = new JsonObject();

                foreach (var (name, value) in source) {
                    kept[name] = ((value is JsonArray items) ? SelectItems(array: items, dropped: ref dropped) : value?.DeepClone());
                }

                return kept;
            }
            default: return node;
        }
    }
    private JsonArray SelectItems(JsonArray array, ref int dropped) {
        var kept = new JsonArray();

        for (var index = 0; (index < array.Count); index++) {
            var number = (index + 1);
            var item = array[index];

            if (((m_ranges is not null) && !m_ranges.Any(range => ((number >= range.First) && (number <= range.Last)))) ||
                ((m_match is not null) && ((item?.ToJsonString() ?? string.Empty).IndexOf(value: m_match, comparisonType: StringComparison.OrdinalIgnoreCase) < 0))) {
                dropped++;

                continue;
            }
            kept.Add(item: item?.DeepClone());
        }

        return kept;
    }
    // Keeps the listed properties wherever they occur, and the containers that lead to one.
    private static JsonNode? Project(string[] fields, JsonNode? node) {
        switch (node) {
            case JsonObject source: {
                var kept = new JsonObject();

                foreach (var (name, value) in source) {
                    if (fields.Contains(value: name, comparer: StringComparer.OrdinalIgnoreCase)) { kept[name] = value?.DeepClone(); }
                    else if (Project(fields: fields, node: value) is { } inner) { kept[name] = inner; }
                }

                return ((kept.Count == 0) ? null : kept);
            }
            case JsonArray source: {
                var kept = new JsonArray();

                foreach (var item in source) {
                    if (Project(fields: fields, node: item) is { } inner) { kept.Add(item: inner); }
                }

                return ((kept.Count == 0) ? null : kept);
            }
            default: return null;
        }
    }
    // A line of key=value tokens keeps the listed ones; a line with none is dropped.
    private static string KeepFields(string text, string[] fields) {
        var kept = new List<string>();

        foreach (var line in text.Split(separator: '\n')) {
            var tokens = line.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries)
                .Where(token => {
                    var equals = token.IndexOf(value: '=');

                    return ((equals > 0) && fields.Contains(value: token[..equals], comparer: StringComparer.OrdinalIgnoreCase));
                }).ToArray();

            if (tokens.Length > 0) { kept.Add(item: string.Join(separator: ' ', value: tokens)); }
        }

        return string.Join(separator: '\n', values: kept);
    }
    private string KeepLines(string text) {
        var all = text.Split(separator: '\n');
        var kept = new List<string>();

        for (var index = 0; (index < all.Length); index++) {
            var number = (index + 1);

            if ((m_ranges is not null) && !m_ranges.Any(range => ((number >= range.First) && (number <= range.Last)))) { continue; }
            if ((m_match is not null) && (all[index].IndexOf(value: m_match, comparisonType: StringComparison.OrdinalIgnoreCase) < 0)) { continue; }
            kept.Add(item: all[index]);
        }

        return ((kept.Count == 0)
            ? $"[puck_exec: no line selected of {all.Length}]"
            : (string.Join(separator: '\n', values: kept) + ((kept.Count < all.Length) ? $"\n[puck_exec: kept {kept.Count} of {all.Length} lines]" : string.Empty)));
    }
    private static string Cut(string text, int cap) {
        var marker = string.Create(provider: CultureInfo.InvariantCulture, handler: $"\n[puck_exec: output truncated at {cap} of {Encoding.UTF8.GetByteCount(s: text)} bytes; narrow with match, lines or fields]");
        var room = (cap - Encoding.UTF8.GetByteCount(s: marker));
        var length = 0;
        var bytes = 0;

        while (length < text.Length) {
            var width = (char.IsHighSurrogate(c: text[length]) && ((length + 1) < text.Length) ? 2 : 1);
            var size = Encoding.UTF8.GetByteCount(chars: text.AsSpan(start: length, length: width));

            if ((bytes + size) > room) { break; }
            bytes += size;
            length += width;
        }
        // A cut between the halves of a surrogate pair would leave an unpaired surrogate that JSON cannot carry.
        if ((length > 0) && char.IsHighSurrogate(c: text[(length - 1)])) { length--; }

        return (text[..length] + marker);
    }
}
