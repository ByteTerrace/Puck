using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
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

        // The source's own tree: the compiled one also holds the declarations its imports bring, at their own offsets.
        var own = PuckParser.ParseDocument(source: source, vocabulary: Lowering.WorldDocumentVocabulary.Instance);

        // A changed value is first rewritten where its text is, which may be a `let` its node reads; when that text
        // writes another node too, the value is written at its own node instead.
        foreach (var inPlace in (bool[])[false, true]) {
            var plan = new Plan(inPlace: inPlace, original: original, own: own, source: source, sourcePath: sourcePath);

            foreach (var changed in Changes(before: original.Json, after: target, pointer: string.Empty)) {
                if (!plan.TryChange(changed: changed, reason: out reason, target: target)) {
                    return false;
                }
            }
            if (!plan.TryApply(candidate: out var candidate, reason: out reason)) {
                return false;
            }

            var check = WorldCompiler.Compile(source: candidate, sourcePath: sourcePath);

            if (check.Success && JsonNode.DeepEquals(node1: check.Json, node2: target)) {
                rewritten = candidate;
                reason = string.Empty;
                return true;
            }
        }
        reason = "rewritten source does not reproduce the requested document; nothing written";
        return false;
    }

    // The edits one pass makes to `source`, which compiled to `original`.
    private sealed class Plan(string source, string? sourcePath, WorldCompilation original, DocumentNode own, bool inPlace) {
        private readonly IReadOnlyDictionary<string, SourceOrigin> m_entries = original.SourceMap.Snapshot();
        private readonly List<Edit> m_edits = [];

        // One changed pointer; `target` holds what the source must lower to there.
        public bool TryChange(string changed, JsonObject target, out string reason) {
            if ((At(root: original.Json, pointer: changed) is JsonArray before) && (At(pointer: changed, root: target) is JsonArray after) &&
                WorldDocumentBasis.TryFindRowKey(ambiguity: out _, basis: before, key: out var key, overlay: after)) {
                return TryRows(after: after, before: before, key: key, pointer: changed, reason: out reason, target: target);
            }

            var pointer = (Exists(pointer: changed, root: target) ? changed : Parent(pointer: changed));

            while ((pointer.Length > 0) && !m_entries.ContainsKey(key: pointer)) {
                pointer = Parent(pointer: pointer);
            }
            if (pointer.Length == 0) {
                var sectionPointer = ("/" + changed.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '/')[0]);

                if (!Exists(root: original.Json!, pointer: sectionPointer) && Exists(pointer: sectionPointer, root: target)) {
                    if (m_edits.Any(predicate: edit => (edit.Pointer == sectionPointer))) { reason = string.Empty; return true; }
                    if (!TryReplacement(pointer: sectionPointer, reason: out reason, target: target, text: out var addition)) { return false; }
                    m_edits.Add(item: new Edit(Pointer: sectionPointer, Span: new SourceSpan(Offset: source.Length, Length: 0, Line: 0, Column: 0), Text: (("\n" + addition) + "\n")));
                    return true;
                }
                if (m_entries.TryGetValue(key: changed, value: out var removed) && removed.DefinesNode && !Exists(pointer: changed, root: target)) {
                    if (!TryEditable(pointer: changed, reason: out reason)) { return false; }
                    m_edits.Add(item: new Edit(Pointer: changed, Span: Removal(source: source, span: removed.Span), Text: string.Empty));
                    return true;
                }
                reason = $"{changed} has no authored node to replace; save a JSON delta with this source as its basis";
                return false;
            }

            string replacement;
            SourceOrigin origin;

            if ((m_entries[pointer].ValueSpan is { } valueSpan) && (At(pointer: pointer, root: target) is JsonValue leaf) &&
                (!inPlace || Contains(inner: valueSpan, outer: DefiningSpan(pointer: pointer)))) {
                origin = m_entries[pointer] with { Span = SourceLexemes.ContentSpan(source: source, span: valueSpan) };
                if (!inPlace && (LetLiteral(span: origin.Span) is { } bound)) {
                    origin = origin with { Span = bound };
                }
                replacement = WorldDecompiler.DecompileValue(value: leaf, form: origin.ValueForm);
            } else {
                reason = string.Empty;
                while (!m_entries[pointer].DefinesNode || !TryReplacement(pointer: pointer, reason: out reason, target: target, text: out replacement)) {
                    pointer = Parent(pointer: pointer);
                    while ((pointer.Length > 0) && !m_entries.ContainsKey(key: pointer)) { pointer = Parent(pointer: pointer); }
                    if (pointer.Length == 0) { return false; }
                }
                origin = m_entries[pointer];
            }

            if (!TryEditable(pointer: pointer, reason: out reason)) { return false; }
            if ((origin.Span.Offset < 0) || (origin.Span.Length <= 0) || (origin.Span.Offset > (source.Length - origin.Span.Length))) {
                reason = $"{changed} has no complete authored span";
                return false;
            }
            if (m_edits.Any(predicate: edit => Contains(outer: edit.Span, inner: origin.Span))) {
                return true;
            }
            _ = m_edits.RemoveAll(match: edit => Contains(outer: origin.Span, inner: edit.Span));
            var indentStart = (source.LastIndexOf(value: '\n', startIndex: Math.Max(val1: 0, val2: (origin.Span.Offset - 1))) + 1);
            var prefix = source[indentStart..origin.Span.Offset];
            var indent = (prefix.All(predicate: static character => (character is ' ' or '\t')) ? prefix : string.Empty);

            m_edits.Add(item: new Edit(Pointer: pointer, Span: origin.Span, Text: replacement.Replace(comparisonType: StringComparison.Ordinal, newValue: ("\n" + indent), oldValue: "\n")));
            return true;
        }
        public bool TryApply(out string candidate, out string reason) {
            var builder = new StringBuilder(value: source);
            var previousStart = source.Length;

            candidate = source;
            foreach (var edit in m_edits.OrderByDescending(keySelector: static edit => edit.Span.Offset)) {
                if ((edit.Span.Offset + edit.Span.Length) > previousStart) {
                    reason = $"{edit.Pointer} overlaps another changed source node";
                    return false;
                }
                _ = builder.Remove(startIndex: edit.Span.Offset, length: edit.Span.Length);
                _ = builder.Insert(index: edit.Span.Offset, value: edit.Text);
                previousStart = edit.Span.Offset;
            }
            candidate = builder.ToString();
            reason = string.Empty;
            return true;
        }

        // A keyed list: a removed row leaves with its lines, a surviving row's changes are made inside it, and a new
        // row joins the list's end. Rows that change order are rewritten slot by slot.
        private bool TryRows(JsonObject target, string pointer, JsonArray before, JsonArray after, string key, out string reason) {
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
                    if (JsonNode.DeepEquals(node1: WorldDocumentBasis.RowIdentity(key: key, row: before[index]), node2: WorldDocumentBasis.RowIdentity(key: key, row: after[next]))) { prior = index; break; }
                }
                if (prior < 0) { appendFrom = Math.Min(val1: appendFrom, val2: next); continue; }
                if ((prior <= lastMatch) || (appendFrom < next)) { preservesOrder = false; break; }
                matched[prior] = next;
                lastMatch = prior;
            }
            if (!preservesOrder) { appendFrom = before.Count; }

            // The target with each surviving row back at the index it had, so a change inside one is read where the
            // source map has its spans.
            var aligned = (JsonObject)target.DeepClone();

            if (preservesOrder && (At(pointer: Parent(pointer: pointer), root: aligned) is { } holder)) {
                holder[Unescape(value: pointer[(pointer.LastIndexOf(value: '/') + 1)..])] = new JsonArray(items: [.. before.Select(selector: (row, index) => ((matched[index] >= 0) ? after[matched[index]] : row)?.DeepClone())]);
            }
            for (var index = 0; (index < before.Count); index++) {
                var next = (preservesOrder ? matched[index] : ((index < after.Count) ? index : -1));

                if ((next >= 0) && JsonNode.DeepEquals(node1: before[index], node2: after[next])) { continue; }
                var oldPointer = ((pointer + "/") + index.ToString(provider: CultureInfo.InvariantCulture));

                if (!m_entries.TryGetValue(key: oldPointer, value: out var origin)) {
                    reason = $"{oldPointer} has no authored span; save a JSON delta with this source as its basis";
                    return false;
                }
                appendAt = Math.Max(val1: appendAt, val2: (origin.Span.Offset + origin.Span.Length));
                var lineStart = (source.LastIndexOf(value: '\n', startIndex: Math.Max(val1: 0, val2: (origin.Span.Offset - 1))) + 1);
                var prefix = source[lineStart..origin.Span.Offset];

                if (prefix.All(predicate: static value => (value is ' ' or '\t'))) { indent = prefix; }
                if (preservesOrder && (next >= 0)) {
                    foreach (var changed in Changes(after: after[next], before: before[index], pointer: oldPointer)) {
                        if (!TryChange(changed: changed, reason: out reason, target: aligned)) { return false; }
                    }
                    continue;
                }
                if (!TryEditable(pointer: oldPointer, reason: out reason)) { return false; }
                var replacement = string.Empty;

                if ((next >= 0) && !TryRowText(after: after, before: before, index: next, key: key, pointer: pointer, reason: out reason, target: target, text: out replacement)) { return false; }
                m_edits.Add(item: new Edit(Pointer: oldPointer, Span: ((next < 0) ? Removal(source: source, span: origin.Span) : origin.Span), Text: replacement.Replace(comparisonType: StringComparison.Ordinal, newValue: ("\n" + indent), oldValue: "\n")));
            }
            var additions = new StringBuilder();

            // A row only added takes the indentation of the rows already there.
            if ((appendAt < 0) && m_entries.TryGetValue(key: (pointer + "/0"), value: out var first)) {
                var firstLine = (source.LastIndexOf(value: '\n', startIndex: Math.Max(val1: 0, val2: (first.Span.Offset - 1))) + 1);
                var firstPrefix = source[firstLine..first.Span.Offset];

                if (firstPrefix.All(predicate: static value => (value is ' ' or '\t'))) { indent = firstPrefix; }
            }
            for (var index = appendFrom; (index < after.Count); index++) {
                if (!TryRowText(after: after, before: before, index: index, key: key, pointer: pointer, reason: out reason, target: target, text: out var replacement)) { return false; }
                _ = additions.Append(value: '\n').Append(value: indent).Append(value: replacement.Replace(comparisonType: StringComparison.Ordinal, newValue: ("\n" + indent), oldValue: "\n"));
            }
            if (additions.Length > 0) {
                var parent = pointer;

                while ((parent.Length > 0) && !m_entries.ContainsKey(key: parent)) { parent = Parent(pointer: parent); }
                if (parent.Length > 0) {
                    var container = m_entries[parent].Span;
                    var close = ((container.Offset + container.Length) - 1);

                    while ((close > container.Offset) && char.IsWhiteSpace(c: source[close])) { close--; }
                    if (source[close] is '}' or ']') {
                        var closeLine = (source.LastIndexOf(value: '\n', startIndex: Math.Max(val1: 0, val2: (close - 1))) + 1);

                        // On the closing line's own start, as lines of their own, when the container closes on a line
                        // of its own; a row removed above ends where they begin.
                        if ((closeLine > container.Offset) && source.AsSpan(start: closeLine, length: (close - closeLine)).IsWhiteSpace()) {
                            _ = additions.Remove(startIndex: 0, length: 1).Append(value: '\n');
                            appendAt = closeLine;
                        } else {
                            appendAt = close;
                            _ = additions.Append(value: '\n');
                        }
                    }
                }
                if (appendAt < 0) { reason = $"{pointer} has no authored section to append to"; return false; }
                m_edits.Add(item: new Edit(Pointer: pointer, Span: new SourceSpan(Column: 0, Length: 0, Line: 0, Offset: appendAt), Text: additions.ToString()));
            }
            reason = string.Empty;
            return true;
        }
        private bool TryRowText(JsonObject target, string pointer, JsonArray before, JsonArray after, string key, int index, out string text, out string reason) {
            text = string.Empty;
            if ((WorldDocumentBasis.RowIdentity(key: key, row: after[index]) is JsonValue identity) && identity.TryGetValue<string>(value: out var name) && Puck.State.GeneratedName.IsGenerated(name: name)) {
                reason = $"row '{name}' has an engine-generated id; rename it to an authored id before saving source, or save a JSON delta with this source as its basis";
                return false;
            }
            for (var at = 0; (at < before.Count); at++) {
                if (!JsonNode.DeepEquals(node1: before[at], node2: after[index])) { continue; }
                var previous = ((pointer + "/") + at.ToString(provider: CultureInfo.InvariantCulture));

                if (!TryEditable(pointer: previous, reason: out reason)) { return false; }
                if (!m_entries.TryGetValue(key: previous, value: out var origin)) { break; }
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
        private bool TryEditable(string pointer, out string reason) {
            foreach (var (at, origin) in m_entries) {
                if ((at != pointer) && !at.StartsWith(comparisonType: StringComparison.Ordinal, value: (pointer + "/"))) { continue; }
                // A span another file or a module instance wrote indexes that file, so this source's tree cannot say
                // what generated it.
                var local = Local(origin: origin, sourcePath: sourcePath);
                var generator = (local
                    ? SyntaxWalk.PathAt(root: own, offset: origin.Span.Offset).FirstOrDefault(predicate: static node => (node is TemplateNode or ForStatementNode or WorldDeclarationNode))
                    : null);

                if (!local || (generator is not null)) {
                    var name = generator switch {
                        TemplateNode template => $"template '{template.Name}'",
                        ForStatementNode => "compile-time for",
                        WorldDeclarationNode => "world module declaration",
                        _ => ((origin.SourcePath is { } module) ? WorldDocumentLabel.Of(path: module) : "a module instance"),
                    };

                    reason = $"{Describe(root: original.Json!, pointer: at)} is generated by {name}; save a JSON delta with this source as its basis";
                    return false;
                }
            }
            reason = string.Empty;
            return true;
        }
        // The literal a top-level `let` binds, when `span` reads that `let` bare: the text a value written through it
        // changes in.
        private SourceSpan? LetLiteral(SourceSpan span) {
            var name = source.Substring(startIndex: span.Offset, length: span.Length);

            return (own.Statements.OfType<LetNode>().LastOrDefault(predicate: let => string.Equals(a: let.Name, b: name, comparisonType: StringComparison.Ordinal))?.Value switch {
                LiteralExpressionNode literal => literal.Span,
                UnaryExpressionNode { Operand: LiteralExpressionNode } negated => negated.Span,
                _ => null,
            });
        }
        // The span of the nearest node at or above `pointer` that defines one.
        private SourceSpan DefiningSpan(string pointer) {
            for (var at = pointer; (at.Length > 0); at = Parent(pointer: at)) {
                if (m_entries.TryGetValue(key: at, value: out var origin) && origin.DefinesNode) {
                    return origin.Span;
                }
            }
            return SourceSpan.None;
        }
    }

    // A removed node that stands alone on its lines takes those lines with it.
    private static SourceSpan Removal(string source, SourceSpan span) {
        var lineStart = (source.LastIndexOf(value: '\n', startIndex: Math.Max(val1: 0, val2: (span.Offset - 1))) + 1);
        var lineEnd = (span.Offset + span.Length);

        if (!source.AsSpan(start: lineStart, length: (span.Offset - lineStart)).IsWhiteSpace()) {
            return span;
        }
        while ((lineEnd < source.Length) && (source[lineEnd] is ' ' or '\t' or '\r')) {
            lineEnd++;
        }
        if ((lineEnd < source.Length) && (source[lineEnd] != '\n')) {
            return span;
        }
        return span with { Length = (Math.Min(val1: (lineEnd + 1), val2: source.Length) - lineStart), Offset = lineStart };
    }
    private static bool Local(SourceOrigin origin, string? sourcePath) => ((origin.ModuleInstancePath is null) &&
        ((origin.SourcePath is null) || ((sourcePath is not null) && Puck.Abstractions.PuckPaths.Comparer.Equals(
            x: Path.GetFullPath(path: origin.SourcePath), y: Path.GetFullPath(path: sourcePath)))));
    private static bool Contains(SourceSpan outer, SourceSpan inner) => ((outer.Offset <= inner.Offset) &&
        ((outer.Offset + outer.Length) >= (inner.Offset + inner.Length)));
    internal static string Parent(string pointer) => pointer[..Math.Max(val1: 0, val2: pointer.LastIndexOf(value: '/'))];
    private static IEnumerable<string> Changes(JsonNode? before, JsonNode? after, string pointer) {
        if (JsonNode.DeepEquals(node1: before, node2: after)) { yield break; }
        if ((before is JsonObject left) && (after is JsonObject right)) {
            foreach (var key in left.Select(selector: static member => member.Key).Union(second: right.Select(selector: static member => member.Key), comparer: StringComparer.Ordinal)) {
                var child = ((pointer + "/") + Escape(value: key));

                if (left.ContainsKey(propertyName: key) != right.ContainsKey(propertyName: key)) { yield return child; } else { foreach (var changed in Changes(before: left[key], after: right[key], pointer: child)) { yield return changed; } }
            }
        } else if ((before is JsonArray first) && (after is JsonArray second) && (first.Count == second.Count)) {
            if (WorldDocumentBasis.TryFindRowKey(ambiguity: out _, basis: first, key: out var key, overlay: second) &&
                first.Where(predicate: (row, index) => !JsonNode.DeepEquals(node1: WorldDocumentBasis.RowIdentity(key: key, row: row), node2: WorldDocumentBasis.RowIdentity(key: key, row: second[index]))).Any()) {
                yield return pointer;
                yield break;
            }
            for (var index = 0; (index < first.Count); index++) {
                foreach (var changed in Changes(before: first[index], after: second[index], pointer: ((pointer + "/") + index.ToString(provider: CultureInfo.InvariantCulture)))) { yield return changed; }
            }
        } else { yield return pointer; }
    }
    internal static string Escape(string value) => value.Replace(comparisonType: StringComparison.Ordinal, newValue: "~0", oldValue: "~")
        .Replace(comparisonType: StringComparison.Ordinal, newValue: "~1", oldValue: "/");
    internal static string Unescape(string value) => value.Replace(comparisonType: StringComparison.Ordinal, newValue: "/", oldValue: "~1")
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
            var decompiled = WorldDecompiler.Decompile(root: ((JsonObject)fragment!));
            // Printed in the source's own layout, so the node reads as its neighbours do.
            var printed = (PuckPrinter.Format(source: decompiled, vocabulary: Lowering.WorldDocumentVocabulary.Instance).Value ?? decompiled);
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
    internal static JsonNode? At(JsonNode? root, string pointer) {
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
}
