using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Decompiler;

namespace Puck.World.Transpiler;

/// <summary>Rewrites changed authored nodes through the world's decompiler and printer while preserving every
/// character outside their source spans. Expansion-owned nodes are refused instead of changing their generator.</summary>
public static class WorldSourceEdits {
    private sealed record Edit(string Pointer, SourceSpan Span, string Text);

    /// <summary>Prepares a source edit without writing a file. The desired document uses the source's own lowered
    /// shape, before basis composition; the whole rewritten source is compiled again before it is returned.</summary>
    /// <param name="source">The source text to retain outside changed nodes.</param>
    /// <param name="target">The desired lowered document.</param>
    /// <param name="rewritten">The verified rewritten text, or the original text on refusal.</param>
    /// <param name="reason">The refusal naming the node that cannot be saved, or empty.</param>
    /// <param name="sourcePath">The source path used to resolve imports and assets.</param>
    /// <returns>Whether every edit can be written without changing unrelated source.</returns>
    public static bool TryRewrite(string source, JsonObject target, out string rewritten, out string reason, string? sourcePath = null) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: target);

        rewritten = source;
        var original = WorldCompiler.Compile(source: source, sourcePath: sourcePath);

        if (!original.Success || (original.Json is null) || (original.Document is null)) {
            reason = ("the source does not compile to one document: " + original.Diagnostics.FormatReport());
            return false;
        }

        var entries = original.SourceMap.Snapshot();
        var edits = new List<Edit>();

        foreach (var changed in Changes(before: original.Json, after: target, pointer: string.Empty)) {
            if ((At(root: original.Json, pointer: changed) is JsonArray before) && (At(pointer: changed, root: target) is JsonArray after) &&
                WorldDocumentBasis.TryFindRowKey(ambiguity: out _, basis: before, key: out _, overlay: after)) {
                if (!TryArrayEdits(after: after, before: before, edits: edits, original: original, pointer: changed, reason: out reason, source: source, sourcePath: sourcePath, target: target)) {
                    return false;
                }
                continue;
            }
            var pointer = (Exists(pointer: changed, root: target) ? changed : Parent(pointer: changed));

            while ((pointer.Length > 0) && !entries.ContainsKey(key: pointer)) {
                pointer = Parent(pointer: pointer);
            }
            if (pointer.Length == 0) {
                var sectionPointer = ("/" + changed.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '/')[0]);

                if (!Exists(root: original.Json, pointer: sectionPointer) && Exists(pointer: sectionPointer, root: target)) {
                    if (edits.Any(predicate: edit => (edit.Pointer == sectionPointer))) { continue; }
                    if (!TryReplacement(pointer: sectionPointer, reason: out reason, target: target, text: out var addition)) { return false; }
                    edits.Add(item: new Edit(Pointer: sectionPointer, Span: new SourceSpan(Offset: source.Length, Length: 0, Line: 0, Column: 0), Text: (("\n" + addition) + "\n")));
                    continue;
                }
                if (entries.TryGetValue(key: changed, value: out var removed) && removed.DefinesNode && !Exists(pointer: changed, root: target)) {
                    if (!TryEditable(original: original, pointer: changed, reason: out reason, sourcePath: sourcePath)) { return false; }
                    edits.Add(item: new Edit(Pointer: changed, Span: removed.Span, Text: string.Empty));
                    continue;
                }
                reason = $"{changed} has no authored node to replace; save a JSON delta with this source as its basis";
                return false;
            }

            string replacement;
            SourceOrigin origin;

            if ((entries[pointer].ValueSpan is { } valueSpan) && (At(root: target, pointer: pointer) is JsonValue leaf)) {
                origin = entries[pointer] with { Span = SourceLexemes.ContentSpan(source: source, span: valueSpan) };
                replacement = WorldDecompiler.DecompileValue(value: leaf, form: origin.ValueForm);
            } else {
                reason = string.Empty;
                while (!entries[pointer].DefinesNode || !TryReplacement(pointer: pointer, reason: out reason, target: target, text: out replacement)) {
                    pointer = Parent(pointer: pointer);
                    while ((pointer.Length > 0) && !entries.ContainsKey(key: pointer)) { pointer = Parent(pointer: pointer); }
                    if (pointer.Length == 0) { return false; }
                }
                origin = entries[pointer];
            }

            if (!TryEditable(original: original, pointer: pointer, reason: out reason, sourcePath: sourcePath)) { return false; }
            if ((origin.Span.Offset < 0) || (origin.Span.Length <= 0) || (origin.Span.Offset > (source.Length - origin.Span.Length))) {
                reason = $"{changed} has no complete authored span";
                return false;
            }
            if (edits.Any(predicate: edit => Contains(outer: edit.Span, inner: origin.Span))) {
                continue;
            }
            _ = edits.RemoveAll(match: edit => Contains(outer: origin.Span, inner: edit.Span));
            var indentStart = (source.LastIndexOf(value: '\n', startIndex: Math.Max(val1: 0, val2: (origin.Span.Offset - 1))) + 1);
            var prefix = source[indentStart..origin.Span.Offset];
            var indent = (prefix.All(predicate: static character => (character is ' ' or '\t')) ? prefix : string.Empty);

            replacement = replacement.Replace(comparisonType: StringComparison.Ordinal, newValue: ("\n" + indent), oldValue: "\n");
            edits.Add(item: new Edit(Pointer: pointer, Span: origin.Span, Text: replacement));
        }

        var builder = new StringBuilder(value: source);
        var previousStart = source.Length;

        foreach (var edit in edits.OrderByDescending(keySelector: static edit => edit.Span.Offset)) {
            if ((edit.Span.Offset + edit.Span.Length) > previousStart) {
                reason = $"{edit.Pointer} overlaps another changed source node";
                return false;
            }
            _ = builder.Remove(startIndex: edit.Span.Offset, length: edit.Span.Length);
            _ = builder.Insert(index: edit.Span.Offset, value: edit.Text);
            previousStart = edit.Span.Offset;
        }
        var candidate = builder.ToString();
        var check = WorldCompiler.Compile(source: candidate, sourcePath: sourcePath);

        if (!check.Success || !JsonNode.DeepEquals(node1: check.Json, node2: target)) {
            reason = "rewritten source does not reproduce the requested document; nothing written";
            return false;
        }
        rewritten = candidate;
        reason = string.Empty;
        return true;
    }

    private static bool Local(SourceOrigin origin, string? sourcePath) => ((origin.ModuleInstancePath is null) &&
        ((origin.SourcePath is null) || ((sourcePath is not null) && Puck.Abstractions.PuckPaths.Comparer.Equals(
            x: Path.GetFullPath(path: origin.SourcePath), y: Path.GetFullPath(path: sourcePath)))));
    private static bool Contains(SourceSpan outer, SourceSpan inner) => ((outer.Offset <= inner.Offset) &&
        ((outer.Offset + outer.Length) >= (inner.Offset + inner.Length)));
    private static string Parent(string pointer) => pointer[..Math.Max(val1: 0, val2: pointer.LastIndexOf(value: '/'))];
    private static IEnumerable<string> Changes(JsonNode? before, JsonNode? after, string pointer) {
        if (JsonNode.DeepEquals(node1: before, node2: after)) { yield break; }
        if ((before is JsonObject left) && (after is JsonObject right)) {
            foreach (var key in left.Select(selector: static member => member.Key).Union(second: right.Select(selector: static member => member.Key), comparer: StringComparer.Ordinal)) {
                var child = ((pointer + "/") + Escape(value: key));

                if (left.ContainsKey(propertyName: key) != right.ContainsKey(propertyName: key)) { yield return child; } else { foreach (var changed in Changes(before: left[key], after: right[key], pointer: child)) { yield return changed; } }
            }
        } else if ((before is JsonArray first) && (after is JsonArray second) && (first.Count == second.Count)) {
            if (WorldDocumentBasis.TryFindRowKey(ambiguity: out _, basis: first, key: out var key, overlay: second) &&
                first.Where(predicate: (row, index) => !JsonNode.DeepEquals(node1: row![key], node2: second[index]![key])).Any()) {
                yield return pointer;
                yield break;
            }
            for (var index = 0; (index < first.Count); index++) {
                foreach (var changed in Changes(before: first[index], after: second[index], pointer: ((pointer + "/") + index.ToString(provider: CultureInfo.InvariantCulture)))) { yield return changed; }
            }
        } else { yield return pointer; }
    }
    private static string Escape(string value) => value.Replace(comparisonType: StringComparison.Ordinal, newValue: "~0", oldValue: "~")
        .Replace(comparisonType: StringComparison.Ordinal, newValue: "~1", oldValue: "/");
    private static string Unescape(string value) => value.Replace(comparisonType: StringComparison.Ordinal, newValue: "/", oldValue: "~1")
        .Replace(comparisonType: StringComparison.Ordinal, newValue: "~", oldValue: "~0");
    private static bool TryReplacement(JsonObject target, string pointer, out string text, out string reason) {
        var parts = pointer.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '/').Select(selector: Unescape).ToArray();
        JsonNode? value = target;
        var arraySteps = new bool[parts.Length];
        var mapped = new string[parts.Length];

        for (var index = 0; (index < parts.Length); index++) {
            arraySteps[index] = (value is JsonArray);
            mapped[index] = (arraySteps[index] ? "0" : Escape(value: parts[index]));
            value = value switch {
                JsonObject obj => obj[parts[index]],
                JsonArray array when (int.TryParse(s: parts[index], provider: CultureInfo.InvariantCulture, result: out var element) && (element >= 0) && (element < array.Count)) => array[element],
                _ => null,
            };
        }
        var fragment = value?.DeepClone();

        for (var index = (parts.Length - 1); (index >= 0); index--) {
            fragment = (arraySteps[index] ? new JsonArray(fragment) : new JsonObject { [parts[index]] = fragment });
        }
        text = string.Empty;
        try {
            ((JsonObject)fragment!)["schema"] = (target["schema"]?.DeepClone() ?? JsonValue.Create(value: WorldDefinition.SchemaVersion));
            var printed = WorldDecompiler.Decompile(root: ((JsonObject)fragment!));
            var compiled = WorldCompiler.Compile(source: printed);
            var mappedPointer = ("/" + string.Join(separator: '/', value: mapped));

            if (!compiled.Success || !compiled.SourceMap.Snapshot().TryGetValue(key: mappedPointer, value: out var origin)) {
                reason = $"{pointer} cannot be printed as one authored node";
                return false;
            }
            text = printed.Substring(startIndex: origin.Span.Offset, length: origin.Span.Length).TrimEnd();
            var lineStart = (printed.LastIndexOf(value: '\n', startIndex: Math.Max(val1: 0, val2: (origin.Span.Offset - 1))) + 1);
            var indent = printed[lineStart..origin.Span.Offset];

            if ((indent.Length > 0) && indent.All(predicate: static character => (character == ' '))) {
                text = text.Replace(comparisonType: StringComparison.Ordinal, newValue: "\n", oldValue: ("\n" + indent));
            }
            reason = string.Empty;
            return true;
        } catch (WorldDecompileRefusedException exception) {
            reason = $"{exception.Message}; rename the generated row before saving source, or save a JSON delta with this source as its basis";
            return false;
        }
    }
    private static JsonNode? At(JsonNode? root, string pointer) {
        foreach (var part in pointer.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '/')) {
            root = root switch {
                JsonObject obj => obj[Unescape(value: part)],
                JsonArray array when (int.TryParse(s: part, provider: CultureInfo.InvariantCulture, result: out var index) && (index >= 0) && (index < array.Count)) => array[index],
                _ => null,
            };
        }
        return root;
    }
    private static bool Exists(JsonObject root, string pointer) {
        if (pointer.Length == 0) { return true; }
        var parent = At(root: root, pointer: Parent(pointer: pointer));
        var last = Unescape(value: pointer[(pointer.LastIndexOf(value: '/') + 1)..]);

        return parent switch {
            JsonObject obj => obj.ContainsKey(propertyName: last),
            JsonArray array => (int.TryParse(s: last, provider: CultureInfo.InvariantCulture, result: out var index) && (index >= 0) && (index < array.Count)),
            _ => false,
        };
    }
    private static string Describe(JsonObject root, string pointer) {
        for (var at = pointer; (at.Length > 0); at = Parent(pointer: at)) {
            if (At(pointer: at, root: root) is JsonObject row) {
                foreach (var key in new[] { "id", "name", "key" }) {
                    if ((row[key] is JsonValue value) && value.TryGetValue<string>(value: out var name)) { return $"{pointer} ('{name}')"; }
                }
            }
        }
        return pointer;
    }
    private static bool TryEditable(WorldCompilation original, string pointer, string? sourcePath, out string reason) {
        foreach (var (at, origin) in original.SourceMap.Snapshot()) {
            if ((at != pointer) && !at.StartsWith(comparisonType: StringComparison.Ordinal, value: (pointer + "/"))) { continue; }
            var generator = SyntaxWalk.PathAt(root: original.Document!, offset: origin.Span.Offset).FirstOrDefault(predicate: static node => (node is TemplateNode or ForStatementNode or WorldDeclarationNode));

            if (!Local(origin: origin, sourcePath: sourcePath) || (generator is not null)) {
                var name = generator switch {
                    TemplateNode template => $"template '{template.Name}'",
                    ForStatementNode => "compile-time for",
                    WorldDeclarationNode => "world module declaration",
                    _ => (origin.SourcePath ?? "a module instance"),
                };

                reason = $"{Describe(root: original.Json!, pointer: at)} is generated by {name}; save a JSON delta with this source as its basis";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }
    private static bool TryArrayEdits(string source, string? sourcePath, WorldCompilation original, JsonObject target, string pointer, JsonArray before, JsonArray after, List<Edit> edits, out string reason) {
        if (!WorldDocumentBasis.TryFindRowKey(ambiguity: out var ambiguity, basis: before, key: out var key, overlay: after)) {
            reason = $"{pointer} cannot be matched by row identity ({ambiguity}); save a JSON delta with this source as its basis";
            return false;
        }
        var entries = original.SourceMap.Snapshot();
        var appendAt = -1;
        var indent = "    ";
        // A deletion must not move an unchanged expansion into another source span. Match surviving identities
        // in place when their order stands and new rows form a suffix; a real reorder still replaces the slots.
        var matched = new int[before.Count];

        Array.Fill(array: matched, value: -1);
        var appendFrom = after.Count;
        var lastMatch = -1;
        var preservesOrder = true;

        for (var next = 0; (next < after.Count); next++) {
            var prior = -1;

            for (var index = 0; (index < before.Count); index++) {
                if (JsonNode.DeepEquals(node1: before[index]![key], node2: after[next]![key])) { prior = index; break; }
            }
            if (prior < 0) { appendFrom = Math.Min(val1: appendFrom, val2: next); continue; }
            if ((prior <= lastMatch) || (appendFrom < next)) { preservesOrder = false; break; }
            matched[prior] = next;
            lastMatch = prior;
        }
        if (!preservesOrder) { appendFrom = before.Count; }

        for (var index = 0; (index < before.Count); index++) {
            var next = (preservesOrder ? matched[index] : ((index < after.Count) ? index : -1));

            if ((next >= 0) && JsonNode.DeepEquals(node1: before[index], node2: after[next])) { continue; }
            var oldPointer = ((pointer + "/") + index.ToString(provider: CultureInfo.InvariantCulture));

            if (!entries.TryGetValue(key: oldPointer, value: out var origin)) {
                reason = $"{oldPointer} has no authored span; save a JSON delta with this source as its basis";
                return false;
            }
            appendAt = Math.Max(val1: appendAt, val2: (origin.Span.Offset + origin.Span.Length));
            var lineStart = (source.LastIndexOf(value: '\n', startIndex: Math.Max(val1: 0, val2: (origin.Span.Offset - 1))) + 1);
            var prefix = source[lineStart..origin.Span.Offset];

            if (prefix.All(predicate: static value => (value is ' ' or '\t'))) { indent = prefix; }
            if (!TryEditable(original: original, pointer: oldPointer, reason: out reason, sourcePath: sourcePath)) { return false; }
            var replacement = string.Empty;

            if ((next >= 0) && !TryRowText(after: after, before: before, index: next, key: key, original: original, pointer: pointer, reason: out reason, source: source, sourcePath: sourcePath, target: target, text: out replacement)) { return false; }
            edits.Add(item: new Edit(Pointer: oldPointer, Span: origin.Span, Text: replacement.Replace(comparisonType: StringComparison.Ordinal, newValue: ("\n" + indent), oldValue: "\n")));
        }
        var additions = new StringBuilder();

        for (var index = appendFrom; (index < after.Count); index++) {
            if (!TryRowText(after: after, before: before, index: index, key: key, original: original, pointer: pointer, reason: out reason, source: source, sourcePath: sourcePath, target: target, text: out var replacement)) { return false; }
            _ = additions.Append(value: '\n').Append(value: indent).Append(value: replacement.Replace(comparisonType: StringComparison.Ordinal, newValue: ("\n" + indent), oldValue: "\n"));
        }
        if (additions.Length > 0) {
            var parent = pointer;

            while ((parent.Length > 0) && !entries.ContainsKey(key: parent)) { parent = Parent(pointer: parent); }
            if (parent.Length > 0) {
                var container = entries[parent].Span;
                var close = ((container.Offset + container.Length) - 1);

                while ((close > container.Offset) && char.IsWhiteSpace(c: source[close])) { close--; }
                if (source[close] is '}' or ']') {
                    appendAt = close;
                    _ = additions.Append(value: '\n');
                }
            }
            if (appendAt < 0) { reason = $"{pointer} has no authored section to append to"; return false; }
            edits.Add(item: new Edit(Pointer: pointer, Span: new SourceSpan(Column: 0, Length: 0, Line: 0, Offset: appendAt), Text: additions.ToString()));
        }
        reason = string.Empty;
        return true;
    }
    private static bool TryRowText(string source, string? sourcePath, WorldCompilation original, JsonObject target, string pointer, JsonArray before, JsonArray after, string key, int index, out string text, out string reason) {
        text = string.Empty;
        if ((after[index]![key] is JsonValue identity) && identity.TryGetValue<string>(value: out var name) && Puck.State.GeneratedName.IsGenerated(name: name)) {
            reason = $"row '{name}' has an engine-generated id; rename it to an authored id before saving source, or save a JSON delta with this source as its basis";
            return false;
        }
        for (var at = 0; (at < before.Count); at++) {
            if (!JsonNode.DeepEquals(node1: before[at], node2: after[index])) { continue; }
            var previous = ((pointer + "/") + at.ToString(provider: CultureInfo.InvariantCulture));

            if (!TryEditable(original: original, pointer: previous, reason: out reason, sourcePath: sourcePath)) { return false; }
            if (!original.SourceMap.Snapshot().TryGetValue(key: previous, value: out var origin)) { break; }
            text = source.Substring(startIndex: origin.Span.Offset, length: origin.Span.Length);
            var lineStart = (source.LastIndexOf(value: '\n', startIndex: Math.Max(val1: 0, val2: (origin.Span.Offset - 1))) + 1);
            var prefix = source[lineStart..origin.Span.Offset];

            if ((prefix.Length > 0) && prefix.All(predicate: static value => (value is ' ' or '\t'))) {
                text = text.Replace(comparisonType: StringComparison.Ordinal, newValue: "\n", oldValue: ("\n" + prefix));
            }
            reason = string.Empty;
            return true;
        }
        return TryReplacement(target: target, pointer: ((pointer + "/") + index.ToString(provider: CultureInfo.InvariantCulture)), text: out text, reason: out reason);
    }
}
