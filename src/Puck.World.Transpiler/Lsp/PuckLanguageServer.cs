using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Editing;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler.Lsp;

/// <summary>The Language Server Protocol (LSP) implementation for the Puck authoring language: completion, hover,
/// document symbols, formatting, semantic tokens and published diagnostics. <see cref="Handle"/> is the protocol and
/// knows no transport; <see cref="RunAsync"/> serves it over stdio framing for <c>puck lsp</c>, and the browser engine
/// hands it one message per call.</summary>
public sealed partial class PuckLanguageServer {
    private readonly string m_catalogFingerprint;
    private readonly Func<DocumentNode, JsonArray?>? m_completeDocument;
    private readonly Func<DocumentNode, string?, DiagnosticBag, bool>? m_diagnoseDocument;
    private readonly IMachineValidationCatalog? m_machines;

    private readonly Dictionary<string, string> m_documents = new(comparer: StringComparer.OrdinalIgnoreCase);
    private bool m_running = true;

    private Action<JsonObject>? m_send;

    private static void AddNode(JsonArray array, JsonNode? node) {
        array.Add(item: node);
    }
    private static JsonObject CreateRuleSymbol(RuleBlockNode rule, string source) {
        var ruleSymbol = CreateSymbol(
            kind: 5,
            name: $"rule \"{rule.Name}\"",
            node: rule,
            source: source
        );
        var children = new JsonArray();

        foreach (var stmt in rule.Statements) {
            var child = stmt switch {
                WhenStatementNode => CreateSymbol(kind: 6, name: "when", node: stmt, source: source),
                LocalStatementNode localStmt => CreateSymbol(kind: 13, name: $"local {localStmt.Name}", node: stmt, source: source),
                DecisionBlockNode => CreateSymbol(kind: 5, name: "decision", node: stmt, source: source),
                PropertyNode propStmt => CreateSymbol(kind: 7, name: propStmt.Name, node: stmt, source: source),
                _ => null,
            };

            if (child is not null) {
                AddNode(array: children, node: child);
            }
        }
        ruleSymbol["children"] = children;
        return ruleSymbol;
    }
    private static JsonObject CreateStateWorldSymbol(BlockNode world, string source) {
        var symbol = CreateSymbol(
            kind: 5,
            name: "world",
            node: world,
            source: source
        );
        var children = new JsonArray();

        foreach (var stmt in world.Statements) {
            var child = stmt switch {
                StateTableDeclarationNode table => CreateStateTableSymbol(source: source, table: table),
                StateSlotDeclarationNode slot => CreateSymbol(kind: 8, name: $"slot {slot.Name}", node: stmt, source: source),
                BlockNode { Identifier: "row" } => CreateSymbol(kind: 8, name: "row", node: stmt, source: source),
                StatePileDeclarationNode pile => CreateStatePileSymbol(pile: pile, source: source),
                StateGridDeclarationNode grid => CreateSymbol(kind: 8, name: $"grid {grid.Name}", node: stmt, source: source),
                _ => null,
            };

            if (child is not null) {
                AddNode(array: children, node: child);
            }
        }

        symbol["children"] = children;
        return symbol;
    }
    private static JsonObject CreateStatePileSymbol(StatePileDeclarationNode pile, string source) {
        var symbol = CreateSymbol(
            kind: 8,
            name: $"pile {pile.Name} of {pile.TokenRow}",
            node: pile,
            source: source
        );
        var children = new JsonArray();

        foreach (var token in pile.Tokens) {
            AddNode(
                array: children,
                node: CreateSymbol(kind: 7, name: token.Key, node: token, source: source)
            );
        }

        symbol["children"] = children;
        return symbol;
    }
    private static JsonObject CreateStateTableSymbol(StateTableDeclarationNode table, string source) {
        var symbol = CreateSymbol(
            kind: 8,
            name: (string.IsNullOrEmpty(value: table.Kind) ? $"table {table.Name}" : $"table {table.Name} : {table.Kind}"),
            node: table,
            source: source
        );
        var children = new JsonArray();

        foreach (var cell in table.Cells) {
            AddNode(
                array: children,
                node: CreateSymbol(kind: 7, name: cell.Key, node: cell, source: source)
            );
        }

        symbol["children"] = children;
        return symbol;
    }
    // A symbol's range is the whole construct, across however many lines it spans; so is its selection range.
    private static JsonObject CreateSymbol(string name, int kind, SyntaxNode node, string source) => new() {
        ["name"] = name,
        ["kind"] = kind,
        ["range"] = LspJson.Range(source: source, span: node.Span),
        ["selectionRange"] = LspJson.Range(source: source, span: node.Span),
    };
    // The words this vocabulary's own construct table does not describe: the document headers, the compile-time
    // layer the core owns, the unit suffixes, and the two cell kinds no described member enumerates on its own.
    private static string? GetDocumentationForWord(string word, string? enclosing, bool memberPosition) => (((
        memberPosition || WorldConstructs.Table.TryGet(
        construct: out _,
        keyword: word
    ))
        ? WorldConstructLanguageServices.Hover(
            enclosing: enclosing,
            table: WorldConstructs.Table,
            word: word
        )
        : null
    ) ?? (word switch {
        "schema" => "**`schema` Directive**\n\nDeclares the document schema family tag (e.g. `puck.world.definition.v1`, `puck.creation.v1`). Enables semantic validation and schema conformance checks.",
        "basis" => "**`basis` Directive**\n\nSpecifies the base world document path inherited by this world definition. Properties in this document override or compose over the basis.",
        "documentId" => "**`documentId` Directive**\n\nUnique string identifier for this world definition document.",
        "let" => "**`let` Declaration**\n\nDeclares a compile-time evaluated constant or alias (e.g. `let tickRate = 0.25s`, `let tableColor = #1b4d3e`).",
        "template" => "**`template` Definition**\n\nDeclares a reusable parametric template block expanded at compile time with default and named arguments.",
        "module" => "**`module` Definition**\n\nDeclares a typed compile-time module. Parameters may require points, angles, assets, modules, pools, rows, or gates.",
        "use" => "**`use` Instantiation**\n\nExpands a module at compile time; an `as` alias prefixes its declarations and internal references.",
        "import" => "**`import` Declaration**\n\nNames a source or document beside this one, never its file (`import \"games/klondike\"`): a module library hands over its `let`s and templates, and anything else is a document composed into this one.",
        "export" => "**`export` Declaration**\n\nDeclares exported world facets (`action`, `binding`, `read`) exposed across the network and to client sessions.",
        "orbit" => "**`orbit(pitch:, yaw:, distance:)`**\n\nConfigures spherical orbit camera positioning relative to the focus target.",
        "fieldOfView" => "**`fieldOfView(degrees:)`**\n\nSets the camera vertical field-of-view in degrees.",
        "Bool" => "**CellKind: `Bool`**\n\nA 0/1 boolean cell.",
        "Text" => "**CellKind: `Text`**\n\nA UTF-16 string cell.",
        "boardShift" => "**`boardShift(mask, lattice, direction)`**\n\nPerforms a directional bitwise shift across a multi-dimensional state lattice.",
        "deg" => "**`deg` Unit**\n\nAngular unit in degrees (automatically converted to radians: `value * PI / 180`).",
        "rad" => "**`rad` Unit**\n\nAngular unit in radians.",
        "s" => "**`s` Unit**\n\nTime duration unit in seconds.",
        "ms" => "**`ms` Unit**\n\nTime duration unit in milliseconds.",
        "min" => "**`min` Unit**\n\nTime duration unit in minutes (converted to seconds: `value * 60`).",
        "h" => "**`h` Unit**\n\nTime duration unit in hours (converted to seconds: `value * 3600`).",
        "hz" => "**`hz` Unit**\n\nFrequency unit in Hertz (e.g. `60hz`, `120hz`).",
        "m" => "**`m` Unit**\n\nSpatial metric unit in meters.",
        _ => null
    }));
    // A catalogue member describes authored syntax, not every identifier sharing its spelling. In particular, a
    // template argument named `size` is not `ground.size`. Property members open a source line or an inline block
    // body and modifiers are followed by `(`; header and body members have no independently hoverable label.
    private static bool IsConstructMemberPosition(string text, int offset, string word, string? enclosing) {
        var table = WorldConstructs.Table;
        var candidates = (((enclosing is not null) && table.TryGet(
            construct: out var owner,
            keyword: enclosing
        ))
            ? owner!.Members.Where(predicate: member => string.Equals(
                a: member.Name,
                b: word,
                comparisonType: StringComparison.Ordinal
            ))
            : table.Owners(name: word).SelectMany(selector: construct => construct.Members.Where(predicate: member => string.Equals(
                a: member.Name,
                b: word,
                comparisonType: StringComparison.Ordinal
            )))
        );
        var members = candidates.ToArray();

        if (members.Length == 0) {
            return false;
        }
        var start = Math.Clamp(value: offset, min: 0, max: text.Length);

        while ((start > 0) && IdentifierSpelling.IsPart(character: text[(start - 1)])) {
            start--;
        }
        var end = Math.Min(val1: text.Length, val2: (start + word.Length));

        while ((end < text.Length) && char.IsWhiteSpace(c: text[end]) && (text[end] is not '\r' and not '\n')) {
            end++;
        }
        var lineStart = (text.LastIndexOf(value: '\n', startIndex: Math.Max(val1: 0, val2: (start - 1))) + 1);
        var first = lineStart;

        while ((first < start) && char.IsWhiteSpace(c: text[first])) {
            first++;
        }
        var previous = (start - 1);

        while ((previous >= lineStart) && char.IsWhiteSpace(c: text[previous])) {
            previous--;
        }
        var opensStatement = ((first == start) || ((previous >= lineStart) && (text[previous] == '{')));

        return members.Any(predicate: member => member.Position switch {
            WorldMemberPosition.Modifier or WorldMemberPosition.Cell => ((end < text.Length) && (text[end] == '(')),
            WorldMemberPosition.Property => (opensStatement && (end < text.Length) && (text[end] is ':' or '[')),
            _ => false,
        });
    }
    // Looks `word` up among the buffer's `state` declarations: first directly in the parsed AST, then in the last
    // published compile's lowered state section (never compiling synchronously on the message thread).
    private string? GetStateHoverCard(string uri, string word, DocumentNode? parsedDocument) {
        if (PuckSymbolResolver.FindStateRowInAst(document: parsedDocument, rowName: word) is { } astRow) {
            if (astRow.Kind == "enum") {
                return $"**`{word}`** — enum\n\nMembers: {string.Join(
                    separator: ", ",
                    values: astRow.Keys.Select(selector: static member => $"`{member}`")
                )}";
            }

            return $"**`{word}`** — state row\n\nKind: `{astRow.Kind}`";
        }

        if (
            !m_lastCompilations.TryGetValue(key: uri, value: out var compilation) ||
            (compilation.Json?["state"] is not JsonObject stateSection)
        ) {
            return null;
        }

        foreach (var (sectionName, section) in stateSection) {
            if (section is not JsonArray rows) {
                continue;
            }
            foreach (var row in rows) {
                if (
                    (row is not JsonObject rowObj) ||
                    (rowObj["name"]?.ToString() != word)
                ) {
                    continue;
                }
                if (sectionName == "enums") {
                    return $"**`{word}`** — enum\n\nMembers: {string.Join(
                        separator: ", ",
                        values: ((rowObj["members"] as JsonArray) ?? []).Select(selector: static member => $"`{member}`")
                    )}";
                }
                var kind = (rowObj["kind"]?.ToString() ?? "?");
                var facets = new List<string>();

                if (rowObj["capacity"] is JsonValue capacity) {
                    facets.Add(item: $"capacity {capacity}");
                }
                if (rowObj["domain"] is JsonValue domain) {
                    facets.Add(item: $"domain {domain}");
                }
                var suffix = ((facets.Count > 0)
                    ? $" ({string.Join(
                        separator: ", ",
                        values: facets
                    )})"
                    : ""
                );

                return $"**`{word}`** — state row\n\nKind: `{kind}`{suffix}";
            }
        }
        return null;
    }
    // Whether `targetLine`/`targetCol` sits right after `<row>.` or `<row>.<partial key>` — a dot-access read
    // (stage 3) with the key half not yet typed, or only partly typed. `row` excludes a leading `$` (a reserved
    // channel keeps its dots, per `ExpressionSpelling`) and a leading digit (a decimal literal's own dot, e.g.
    // `0.25`), and requires at least one character before the dot.
    private static bool TryGetDotAccessRowName(string text, int targetLine, int targetCol, out string rowName) {
        rowName = "";
        var lines = text.Split('\n');

        if (
            (targetLine < 0) ||
            (targetLine >= lines.Length)
        ) {
            return false;
        }

        var lineText = lines[targetLine];
        var col = Math.Clamp(
            max: lineText.Length,
            min: 0,
            value: targetCol
        );
        var pos = col;

        while (
            (pos > 0) &&
            IdentifierSpelling.IsPart(character: lineText[(pos - 1)])
        ) {
            pos--;
        }

        if (
            (pos == 0) ||
            (lineText[(pos - 1)] != '.')
        ) {
            return false;
        }

        var rowEnd = (pos - 1);
        var rowStart = rowEnd;

        while (
            (rowStart > 0) &&
            IdentifierSpelling.IsPart(character: lineText[(rowStart - 1)])
        ) {
            rowStart--;
        }
        if (rowStart == rowEnd) {
            return false;
        }

        var candidate = lineText.Substring(
            length: (rowEnd - rowStart),
            startIndex: rowStart
        );

        if (
            char.IsDigit(c: candidate[0]) ||
            ((rowStart > 0) && (lineText[(rowStart - 1)] is '$' or '`'))
        ) {
            return false;
        }

        rowName = candidate;
        return true;
    }
    // One buffer text's vocabulary and whole-text parse. An open document's text is replaced on every change, never
    // edited in place, so the parse is kept beside the text object it was read from: a request finds it again exactly
    // while that buffer is current, and every reader of one buffer (completion, hover, definition, the outline) parses
    // it once between edits.
    private sealed record ParsedBuffer(IDocumentVocabulary Vocabulary, DocumentNode? Document);

    private readonly Puck.Abstractions.CodeDerivedTable<string, ParsedBuffer> m_parsed = new();

    private ParsedBuffer Parsed(string text) => m_parsed.GetValue(key: text, derive: buffer => {
        var vocabulary = m_vocabularyResolver.Resolve(source: buffer);

        try {
            return new ParsedBuffer(Document: PuckParser.ParseDocumentWithDiagnostics(source: buffer, vocabulary: vocabulary).Value, Vocabulary: vocabulary);
        } catch {
            return new ParsedBuffer(Document: null, Vocabulary: vocabulary);
        }
    });
    // Best-effort, recovery-tolerant parse for a buffer mid-edit: a clean parse covers the common case (the
    // dotted read sits inside an already-closed rule), and the fallback truncates at the cursor and synthesizes
    // the closing braces/brackets/parens the truncated prefix is still owed, so an unclosed rule or block being
    // typed for the first time still parses far enough to see its declared rows.
    private DocumentNode? TryParseDocumentBestEffort(string text, int cursorOffset) {
        if (Parsed(text: text).Document is { } direct) {
            return direct;
        }

        try {
            var recovered = RecoverToCursor(
                cursorOffset: cursorOffset,
                text: text
            );

            return PuckParser.ParseDocumentWithDiagnostics(source: recovered, vocabulary: m_vocabularyResolver.Resolve(source: recovered)).Value;
        } catch {
            return null;
        }
    }
    // The text before the cursor with every delimiter it left open closed, which parses when the whole buffer,
    // mid-edit, does not.
    private static string RecoverToCursor(string text, int cursorOffset) {
        var prefix = text[..Math.Clamp(
            max: text.Length,
            min: 0,
            value: cursorOffset
        )];

        return (prefix + ComputeClosingSuffix(source: prefix));
    }
    // Recovery-only lowering compiled without a path key (`sourcePath: null`), so a truncated mid-edit prefix never
    // evicts the real buffer's held compile in `WorldCompileSession`.
    private JsonObject? LowerStateSectionWithoutPathKey(string text) {
        if (m_vocabularyResolver.Resolve(source: text) is not WorldDocumentVocabulary vocabulary) {
            return null;
        }

        return (m_session.CompileBuffer(
            source: text,
            sourcePath: null,
            vocabulary: vocabulary
        ).Json?["state"] as JsonObject);
    }
    // The closing `}`/`]`/`)` sequence, innermost first, that balances every delimiter `source` opened and never
    // closed, outside its strings, backquoted names and comments (SourceLexemes.End, the formatter's own scan).
    private static string ComputeClosingSuffix(string source) {
        var stack = new Stack<char>();
        var i = 0;

        while (i < source.Length) {
            var lexeme = SourceLexemes.End(offset: i, source: source);

            if (lexeme > i) {
                i = lexeme;
                continue;
            }
            var c = source[i];

            switch (c) {
                case '{':
                    stack.Push(item: '}');
                    break;
                case '[':
                    stack.Push(item: ']');
                    break;
                case '(':
                    stack.Push(item: ')');
                    break;
                case '}' or ']' or ')':
                    if (stack.Count > 0) {
                        stack.Pop();
                    }
                    break;
            }
            i++;
        }

        var sb = new StringBuilder();

        while (stack.Count > 0) {
            sb.Append(value: '\n').Append(value: stack.Pop());
        }
        return sb.ToString();
    }
    // Looks `rowName` up as a declared `state` row (world, body, or identity) and lists its cell keys as
    // completion items. Reads from the parsed AST first, then the last published compile, and only falls back to
    // compiling recovery text without a path key so the held compile is never evicted.
    private JsonArray? GetStateRowKeyCompletions(string uri, string text, string rowName, int cursorOffset, DocumentNode? parsedDocument) {
        try {
            if (
                (PuckSymbolResolver.FindStateRowInAst(document: parsedDocument, rowName: rowName) is { Keys.Count: > 0 } astRow) &&
                (astRow.Kind != "enum")
            ) {
                var astItems = new JsonArray();

                foreach (var key in astRow.Keys) {
                    LspJson.AddCompletion(
                        detail: $"state cell — {rowName}.{key}",
                        insertText: key,
                        items: astItems,
                        kind: 5,
                        label: key
                    );
                }
                return astItems;
            }

            var stateSection = ((m_lastCompilations.TryGetValue(key: uri, value: out var lastComp) ? (lastComp.Json?["state"] as JsonObject) : null)
                ?? LowerStateSectionWithoutPathKey(text: RecoverToCursor(cursorOffset: cursorOffset, text: text)));

            if (stateSection is null) {
                return null;
            }
            foreach (var (_, section) in stateSection) {
                if (section is not JsonArray rows) {
                    continue;
                }
                foreach (var row in rows) {
                    if (
                        (row is not JsonObject rowObj) ||
                        (rowObj["name"]?.ToString() != rowName) ||
                        (rowObj["cells"] is not JsonArray cells)
                    ) {
                        continue;
                    }

                    var items = new JsonArray();

                    foreach (var cell in cells) {
                        if (
                            (cell is JsonObject cellObj) &&
                            (cellObj["key"]?.ToString() is { } key)
                        ) {
                            LspJson.AddCompletion(
                                detail: $"state cell — {rowName}.{key}",
                                insertText: key,
                                items: items,
                                kind: 5,
                                label: key
                            );
                        }
                    }
                    return items;
                }
            }
        } catch {
            return null;
        }
        return null;
    }
    private static bool IsAfterNumber(string text, int cursorOffset) {
        var pos = Math.Clamp(max: text.Length, min: 0, value: cursorOffset);

        while ((pos > 0) && char.IsLetter(c: text[(pos - 1)])) {
            pos--;
        }
        return ((pos > 0) && char.IsDigit(c: text[(pos - 1)]));
    }
    private static bool IsValuePosition(string text, int cursorOffset) {
        var limit = Math.Clamp(max: text.Length, min: 0, value: cursorOffset);
        var lineStart = ((limit == 0) ? 0 : (text.LastIndexOf(startIndex: (limit - 1), value: '\n') + 1));
        var linePrefix = text[lineStart..limit];

        // Inside a line after `:` or `=` (and not inside a `{` block opened after it).
        var lastAssign = linePrefix.LastIndexOfAny(anyOf: [':', '=']);

        if (lastAssign < 0) {
            return false;
        }
        return (linePrefix[(lastAssign + 1)..].IndexOf(value: '{') < 0);
    }
    private static bool IsGateLine(string text, int cursorOffset) {
        var limit = Math.Clamp(max: text.Length, min: 0, value: cursorOffset);
        var lineStart = ((limit == 0) ? 0 : (text.LastIndexOf(startIndex: (limit - 1), value: '\n') + 1));
        var trimmed = text[lineStart..limit].TrimStart();

        return (
            trimmed.StartsWith(comparisonType: StringComparison.Ordinal, value: "when ") ||
            trimmed.StartsWith(comparisonType: StringComparison.Ordinal, value: "if ") ||
            trimmed.StartsWith(comparisonType: StringComparison.Ordinal, value: "expect ")
        );
    }
    private static void AddAuthorSymbolCompletions(JsonArray items, DocumentNode? document, IReadOnlyList<PuckSymbolResolver.ImportedSymbol> imported, bool inRule) {
        var offered = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (document is not null) {
            foreach (var stmt in document.Statements) {
                switch (stmt) {
                    case LetNode letNode when offered.Add(item: letNode.Name):
                        if (letNode.Value is FunctionExpressionNode fn) {
                            var fnArgs = string.Join(separator: ", ", values: fn.Parameters.Select(selector: static (p, idx) => $"${{{(idx + 1)}:{p.Name}}}"));

                            LspJson.AddCompletion(
                                detail: $"Function: {letNode.Name}({string.Join(separator: ", ", values: fn.Parameters.Select(selector: static p => p.Name))})",
                                insertText: $"{letNode.Name}({fnArgs})",
                                items: items,
                                kind: 3,
                                label: letNode.Name
                            );
                        } else {
                            LspJson.AddCompletion(
                                detail: "Constant (let)",
                                insertText: letNode.Name,
                                items: items,
                                kind: 21,
                                label: letNode.Name
                            );
                        }
                        break;

                    case TemplateNode tmpl when ((tmpl.IsRuleBody == inRule) && offered.Add(item: tmpl.Name)):
                        AddTemplateCompletion(items: items, label: tmpl.Name, origin: null, template: tmpl);
                        break;

                    case ImportNode importNode when ((importNode.Alias is { Length: > 0 } alias) && offered.Add(item: alias)):
                        LspJson.AddCompletion(
                            detail: $"Import alias for \"{importNode.Path}\"",
                            insertText: alias,
                            items: items,
                            kind: 9,
                            label: alias
                        );
                        break;
                }
            }
        }

        foreach (var symbol in imported) {
            // Aliased symbols are completed after `alias.`; offer unaliased imported symbols directly.
            if (symbol.Name.Contains(value: '.') || !offered.Add(item: symbol.Name)) {
                continue;
            }
            var origin = Path.GetFileName(path: symbol.SourcePath);

            switch (symbol.Node) {
                case LetNode importedLet:
                    LspJson.AddCompletion(
                        detail: $"Constant from {origin}",
                        insertText: symbol.Name,
                        items: items,
                        kind: 21,
                        label: symbol.Name
                    );
                    break;
                case TemplateNode importedTmpl when (importedTmpl.IsRuleBody == inRule):
                    AddTemplateCompletion(items: items, label: symbol.Name, origin: origin, template: importedTmpl);
                    break;
            }
        }
    }
    private static void AddTemplateCompletion(JsonArray items, string label, TemplateNode template, string? origin) {
        var kindLabel = (template.IsModule
            ? "Module"
            : (template.IsRuleBody ? "Rule template" : (template.IsShade ? "Shade template" : "Template")));
        var paramList = string.Join(separator: ", ", values: template.Parameters.Select(selector: static p => p.Name));
        var required = template.Parameters.Where(predicate: static p => (p.DefaultValue is null)).ToArray();
        var snippetArgs = string.Join(separator: ", ", values: required.Select(selector: static (p, idx) => $"{p.Name}: ${{{(idx + 1)}:{p.Name}}}"));
        var fromSuffix = ((origin is null) ? "" : $" ({origin})");

        LspJson.AddCompletion(
            detail: $"{kindLabel}{fromSuffix}: {label}({paramList})",
            insertText: $"{label}({snippetArgs})",
            items: items,
            kind: 3,
            label: label
        );
    }
    private static JsonArray? GetAliasMemberCompletions(string alias, IReadOnlyList<PuckSymbolResolver.ImportedSymbol> imported, bool inRule) {
        var prefix = (alias + ".");
        var items = new JsonArray();

        foreach (var symbol in imported) {
            if (!symbol.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: prefix)) {
                continue;
            }

            var memberName = symbol.Name[prefix.Length..];

            if (memberName.Contains(value: '.')) {
                continue;
            }

            var origin = Path.GetFileName(path: symbol.SourcePath);

            switch (symbol.Node) {
                case LetNode:
                    LspJson.AddCompletion(
                        detail: $"Constant from {origin}",
                        insertText: memberName,
                        items: items,
                        kind: 21,
                        label: memberName
                    );
                    break;
                case TemplateNode tmpl when (tmpl.IsRuleBody == inRule):
                    AddTemplateCompletion(items: items, label: memberName, origin: origin, template: tmpl);
                    break;
            }
        }

        return ((items.Count > 0) ? items : null);
    }
    private void HandleCompletion(JsonNode? id, JsonObject? @params) {
        var uri = (@params?["textDocument"]?["uri"]?.ToString() ?? "");

        if (
            (m_completeDocument is not null) &&
            m_documents.TryGetValue(
            key: uri,
            value: out var source
        ) &&
            (Parsed(text: source).Document is { } document) &&
            (m_completeDocument(document) is { } specialized)
        ) {
            SendResponse(
                id: id,
                result: new JsonObject { ["isIncomplete"] = false, ["items"] = specialized }
            );
            return;
        }

        var line = (((int?)@params?["position"]?["line"]) ?? 0);
        var col = (((int?)@params?["position"]?["character"]) ?? 0);
        var completionText = (m_documents.TryGetValue(
            key: uri,
            value: out var openText
        )
            ? openText
            : ""
        );
        var cursorOffset = LspJson.Offset(character: col, line: line, source: completionText);
        var enclosing = WorldConstructLanguageServices.ConstructAt(
            offset: cursorOffset,
            table: WorldConstructs.Table,
            text: completionText
        );
        var inRule = ((enclosing is not null) && WorldConstructs.Table.TryGet(construct: out var holder, keyword: enclosing) && (holder?.HoldsEffects == true));
        var parsedDocument = ((completionText.Length > 0) ? TryParseDocumentBestEffort(cursorOffset: cursorOffset, text: completionText) : null);
        var vocabulary = Parsed(text: completionText).Vocabulary;
        var completionSourcePath = (TryGetLocalPath(path: out var localCompPath, uri: uri) ? localCompPath : null);
        var imported = PuckSymbolResolver.CollectImportedDeclarations(
            document: parsedDocument,
            sourcePath: completionSourcePath,
            sources: m_session.Sources,
            vocabulary: vocabulary
        );

        // Dot-access completion: import alias members (`alias.`) or declared state row cell keys (`row.`).
        if (
            (completionText.Length > 0) &&
            TryGetDotAccessRowName(
                rowName: out var dotPrefix,
                targetCol: col,
                targetLine: line,
                text: completionText
            )
        ) {
            if (GetAliasMemberCompletions(alias: dotPrefix, imported: imported, inRule: inRule) is { Count: > 0 } aliasItems) {
                SendResponse(
                    id: id,
                    result: new JsonObject { ["isIncomplete"] = false, ["items"] = aliasItems }
                );
                return;
            }

            var keyItems = GetStateRowKeyCompletions(
                cursorOffset: cursorOffset,
                parsedDocument: parsedDocument,
                rowName: dotPrefix,
                text: completionText,
                uri: uri
            );

            if (keyItems is { Count: > 0 }) {
                SendResponse(
                    id: id,
                    result: new JsonObject { ["isIncomplete"] = false, ["items"] = keyItems }
                );
                return;
            }
        }

        var items = new JsonArray();

        // Unit suffixes apply only immediately after a numeric literal.
        if (IsAfterNumber(cursorOffset: cursorOffset, text: completionText)) {
            AddUnitCompletions(items: items);
            SendResponse(
                id: id,
                result: new JsonObject { ["isIncomplete"] = false, ["items"] = items }
            );
            return;
        }

        var inValue = IsValuePosition(cursorOffset: cursorOffset, text: completionText);
        var inGate = (IsGateLine(cursorOffset: cursorOffset, text: completionText) || (enclosing == "expect"));
        var atRoot = ((enclosing is null) && !inValue && !inGate);

        PuckEmbeddingLsp.AddCompletions(items: items);
        if (!inValue && !inGate) {
            WorldConstructLanguageServices.AddCompletions(
                enclosing: enclosing,
                items: items,
                scopeToRoot: true,
                table: WorldConstructs.Table
            );
        }

        AddAuthorSymbolCompletions(
            document: parsedDocument,
            imported: imported,
            inRule: inRule,
            items: items
        );

        if (atRoot) {
            LspJson.AddCompletion(
                detail: "Directive: Schema declaration",
                insertText: $"schema: \"{WorldDefinition.SchemaVersion}\"",
                items: items,
                kind: 14,
                label: "schema"
            );
            LspJson.AddCompletion(
                detail: "Directive: Base world inheritance",
                insertText: "basis: \"worlds/base\"",
                items: items,
                kind: 14,
                label: "basis"
            );
            LspJson.AddCompletion(
                detail: "Directive: Document ID tag",
                insertText: "documentId: \"my-world-id\"",
                items: items,
                kind: 14,
                label: "documentId"
            );
            LspJson.AddCompletion(
                detail: "Keyword: Parametric template",
                insertText: "template ${1:name}(${2:params}) {\n    $0\n}",
                items: items,
                kind: 14,
                label: "template"
            );
            LspJson.AddCompletion(
                detail: "Keyword: Typed module",
                insertText: "module ${1:name}(${2:params}) {\n    $0\n}",
                items: items,
                kind: 14,
                label: "module"
            );
            LspJson.AddCompletion(
                detail: "Keyword: Instantiate module",
                insertText: "use ${1:module} as ${2:alias}(${3:arguments})",
                items: items,
                kind: 14,
                label: "use"
            );
            LspJson.AddCompletion(
                detail: "Keyword: Import",
                insertText: "import \"${1:name}\" as ${2:alias}",
                items: items,
                kind: 14,
                label: "import"
            );
            LspJson.AddCompletion(
                detail: "Keyword: Facet export",
                insertText: "export ${1|action,binding,read|} ${2:names}",
                items: items,
                kind: 14,
                label: "export"
            );

            // The array spelling of a row construct. A container takes no `:` — `materials: [` is PUCK040.
            LspJson.AddCompletion(
                detail: "Section: Surface materials",
                insertText: "materials [\n    $0\n]",
                items: items,
                kind: 7,
                label: "materials"
            );
            LspJson.AddCompletion(
                detail: "Section: Reactive state rules",
                insertText: "rules [\n    $0\n]",
                items: items,
                kind: 7,
                label: "rules"
            );
            LspJson.AddCompletion(
                detail: "Section: WASM Addons",
                insertText: "addons [\n    $0\n]",
                items: items,
                kind: 7,
                label: "addons"
            );
        }

        if (!inValue && !inGate && !inRule) {
            LspJson.AddCompletion(
                detail: "Keyword: Declare constant",
                insertText: "let ${1:name} = ${2:value}",
                items: items,
                kind: 14,
                label: "let"
            );
        }

        if (inValue || (enclosing is "camera" or "cameras")) {
            LspJson.AddCompletion(
                detail: "Camera orbit operation",
                insertText: "orbit(pitch: ${1:0deg}, yaw: ${2:0deg}, distance: ${3:2.5m})",
                items: items,
                kind: 3,
                label: "orbit"
            );
            LspJson.AddCompletion(
                detail: "Camera field-of-view operation",
                insertText: "fieldOfView(degrees: ${1:60})",
                items: items,
                kind: 3,
                label: "fieldOfView"
            );
        }

        if (inValue || inRule || inGate) {
            LspJson.AddCompletion(
                detail: "Bitwise lattice shift function",
                insertText: "boardShift(${1:mask}, ${2:lattice}, ${3:dir})",
                items: items,
                kind: 3,
                label: "boardShift"
            );
            LspJson.AddCompletion(
                detail: "Math clamp function",
                insertText: "clamp(${1:val}, ${2:min}, ${3:max})",
                items: items,
                kind: 3,
                label: "clamp"
            );
        }

        if (inRule || inGate) {
            LspJson.AddCompletion(
                detail: "Keyword: Gate conjunction",
                insertText: "and",
                items: items,
                kind: 14,
                label: "and"
            );
            LspJson.AddCompletion(
                detail: "Keyword: Gate disjunction",
                insertText: "or",
                items: items,
                kind: 14,
                label: "or"
            );
            LspJson.AddCompletion(
                detail: "Keyword: Gate negation",
                insertText: "not ${1:condition}",
                items: items,
                kind: 14,
                label: "not"
            );
        }

        if (inRule && !inGate) {
            LspJson.AddCompletion(
                detail: "Keyword: Rule/option gate",
                insertText: "when ${1:condition}",
                items: items,
                kind: 14,
                label: "when"
            );
            LspJson.AddCompletion(
                detail: "Keyword: 'if' alternate branch",
                insertText: "else {\n    $0\n}",
                items: items,
                kind: 14,
                label: "else"
            );
            LspJson.AddCompletion(
                detail: "Effect: setState",
                insertText: "setState(state: ${1:row}, value: ${2:0})",
                items: items,
                kind: 3,
                label: "setState"
            );
            LspJson.AddCompletion(
                detail: "Effect: addState",
                insertText: "addState(state: ${1:row}, value: ${2:0})",
                items: items,
                kind: 3,
                label: "addState"
            );
            LspJson.AddCompletion(
                detail: "Effect: pushState",
                insertText: "pushState(state: ${1:row}, value: ${2:0})",
                items: items,
                kind: 3,
                label: "pushState"
            );
            LspJson.AddCompletion(
                detail: "Effect: removeStateCell",
                insertText: "removeStateCell(state: ${1:row})",
                items: items,
                kind: 3,
                label: "removeStateCell"
            );
            LspJson.AddCompletion(
                detail: "Effect: scheduleState",
                insertText: "scheduleState(state: ${1:row}, delaySeconds: ${2:1})",
                items: items,
                kind: 3,
                label: "scheduleState"
            );
        }

        if (inValue || (enclosing is "state" or "world" or "table" or "slot" or "grid" or "record")) {
            LspJson.AddCompletion(
                detail: "CellKind: exact integer domain",
                insertText: "Int",
                items: items,
                kind: 13,
                label: "Int"
            );
            LspJson.AddCompletion(
                detail: "CellKind: fixed-point domain",
                insertText: "Fixed",
                items: items,
                kind: 13,
                label: "Fixed"
            );
            LspJson.AddCompletion(
                detail: "CellKind: boolean domain",
                insertText: "Bool",
                items: items,
                kind: 13,
                label: "Bool"
            );
            LspJson.AddCompletion(
                detail: "CellKind: string domain",
                insertText: "Text",
                items: items,
                kind: 13,
                label: "Text"
            );
        }

        SendResponse(
            id: id,
            result: new JsonObject {
                ["isIncomplete"] = false,
                ["items"] = items,
            }
        );
    }
    private static void AddUnitCompletions(JsonArray items) {
        LspJson.AddCompletion(detail: "Unit: Seconds", insertText: "s", items: items, kind: 11, label: "s");
        LspJson.AddCompletion(detail: "Unit: Milliseconds", insertText: "ms", items: items, kind: 11, label: "ms");
        LspJson.AddCompletion(detail: "Unit: Minutes", insertText: "min", items: items, kind: 11, label: "min");
        LspJson.AddCompletion(detail: "Unit: Hours", insertText: "h", items: items, kind: 11, label: "h");
        LspJson.AddCompletion(detail: "Unit: Hertz (frequency)", insertText: "hz", items: items, kind: 11, label: "hz");
        LspJson.AddCompletion(detail: "Unit: Radians", insertText: "rad", items: items, kind: 11, label: "rad");
        LspJson.AddCompletion(detail: "Unit: Degrees", insertText: "deg", items: items, kind: 11, label: "deg");
        LspJson.AddCompletion(detail: "Unit: Meters", insertText: "m", items: items, kind: 11, label: "m");
        LspJson.AddCompletion(detail: "Unit: Centimeters", insertText: "cm", items: items, kind: 11, label: "cm");
        LspJson.AddCompletion(detail: "Unit: Millimeters", insertText: "mm", items: items, kind: 11, label: "mm");
    }
    private void HandleDocumentSymbol(JsonNode? id, JsonObject? @params) {
        var uri = (@params?["textDocument"]?["uri"]?.ToString() ?? "");

        if (!m_documents.TryGetValue(
            key: uri,
            value: out var text
        )) {
            SendResponse(
                id: id,
                result: new JsonArray()
            );
            return;
        }

        var docNode = Parsed(text: text).Document;

        if (docNode is null) {
            SendResponse(
                id: id,
                result: new JsonArray()
            );
            return;
        }

        var symbols = new JsonArray();

        foreach (var stmt in docNode.Statements) {
            if (stmt is BlockNode block) {
                var blockSym = CreateSymbol(
                    kind: 5,
                    name: ((block.Name is not null) ? $"{block.Identifier} \"{block.Name}\"" : block.Identifier),
                    node: block,
                    source: text
                );
                var children = new JsonArray();
                var inState = string.Equals(
                    a: block.Identifier,
                    b: "state",
                    comparisonType: StringComparison.OrdinalIgnoreCase
                );

                foreach (var child in block.Statements) {
                    var childSym = child switch {
                        BlockNode { Identifier: "world", Name: null, Target: null } worldBlock when inState => CreateStateWorldSymbol(source: text, world: worldBlock),
                        BlockNode { Identifier: "spaces", Name: null, Target: null } spacesBlock when inState => PuckEmbeddingLsp.CreateSpacesSymbol(source: text, spaces: spacesBlock),
                        BlockNode childBlock => CreateSymbol(
                            kind: 5,
                            name: ((childBlock.Name is not null) ? $"{childBlock.Identifier} \"{childBlock.Name}\"" : childBlock.Identifier),
                            node: childBlock,
                            source: text
                        ),
                        PropertyNode childProp => CreateSymbol(kind: 7, name: childProp.Name, node: childProp, source: text),
                        _ => null,
                    };

                    if (childSym is not null) {
                        AddNode(array: children, node: childSym);
                    }
                }
                blockSym["children"] = children;
                AddNode(
                    array: symbols,
                    node: blockSym
                );
                continue;
            }

            var symbol = stmt switch {
                PropertyNode prop => CreateSymbol(kind: 7, name: prop.Name, node: prop, source: text),
                LetNode letNode => CreateSymbol(kind: 13, name: $"let {letNode.Name}", node: letNode, source: text),
                TemplateNode tmpl => CreateSymbol(kind: 11, name: $"template {tmpl.Name}", node: tmpl, source: text),
                RuleBlockNode ruleBlock => CreateRuleSymbol(rule: ruleBlock, source: text),
                _ => null,
            };

            if (symbol is not null) {
                AddNode(array: symbols, node: symbol);
            }
        }

        SendResponse(
            id: id,
            result: symbols
        );
    }
    private void HandleFormatting(JsonNode? id, JsonObject? @params) {
        var uri = (@params?["textDocument"]?["uri"]?.ToString() ?? "");

        if (!m_documents.TryGetValue(
            key: uri,
            value: out var text
        )) {
            SendResponse(
                id: id,
                result: new JsonArray()
            );
            return;
        }

        // The request's tabSize and insertSpaces are not read: a source has one layout, and the editor receives exactly
        // the text `puck format` writes.
        var printed = PuckPrinter.Format(
            source: text,
            vocabulary: m_vocabularyResolver.Resolve(source: text)
        );

        // A document that does not parse has no tree to print, so the editor keeps what the author is typing.
        // An already-formatted document returns no edit (`[]`) rather than replacing the whole buffer unchanged.
        if ((printed.Value is not { } formatted) || string.Equals(a: formatted, b: text, comparisonType: StringComparison.Ordinal)) {
            SendResponse(
                id: id,
                result: new JsonArray()
            );

            return;
        }

        var edits = new JsonArray();

        AddNode(
            array: edits,
            node: new JsonObject {
                ["range"] = LspJson.Range(
                    end: LspJson.PositionOf(offset: text.Length, source: text),
                    start: (0, 0)
                ),
                ["newText"] = formatted,
            }
        );

        SendResponse(
            id: id,
            result: edits
        );
    }
    private void HandleHover(JsonNode? id, JsonObject? @params) {
        var uri = (@params?["textDocument"]?["uri"]?.ToString() ?? "");
        var line = (@params?["position"]?["line"]?.GetValue<int>() ?? 0);
        var col = (@params?["position"]?["character"]?.GetValue<int>() ?? 0);

        if (!m_documents.TryGetValue(
            key: uri,
            value: out var text
        )) {
            SendResponse(
                id: id,
                result: null
            );
            return;
        }

        var wordContext = PuckSymbolResolver.WordContextAt(
            targetCol: col,
            targetLine: line,
            text: text
        );
        var word = wordContext.Word;
        var qualified = wordContext.Qualified;
        var offset = LspJson.Offset(
            character: col,
            line: line,
            source: text
        );
        var enclosing = WorldConstructLanguageServices.ConstructAt(
            offset: offset,
            table: WorldConstructs.Table,
            text: text
        );
        string? hoverSourcePath = null;

        if (TryGetLocalPath(path: out var localHoverPath, uri: uri)) {
            hoverSourcePath = localHoverPath;
        }

        var embeddingCard = PuckEmbeddingLsp.GetEmbeddingHoverCard(
            offset: offset,
            sourcePath: hoverSourcePath,
            text: text,
            word: word
        );

        if (embeddingCard is not null) {
            SendResponse(
                id: id,
                result: new JsonObject {
                    ["contents"] = new JsonObject {
                        ["kind"] = "markdown",
                        ["value"] = embeddingCard,
                    },
                }
            );
            return;
        }

        if (string.IsNullOrEmpty(value: word) && string.IsNullOrEmpty(value: qualified)) {
            SendResponse(
                id: id,
                result: null
            );
            return;
        }

        var (vocabulary, parsedDocument) = Parsed(text: text);
        var imported = PuckSymbolResolver.CollectImportedDeclarations(
            document: parsedDocument,
            sourcePath: hoverSourcePath,
            sources: m_session.Sources,
            vocabulary: vocabulary
        );

        var docCard = PuckHoverInfo.Declaration(
            imported: imported,
            offset: offset,
            parsedDocument: parsedDocument,
            qualified: qualified,
            readSource: ReadSourceText,
            resolver: m_vocabularyResolver,
            source: text,
            word: word
        );

        if ((docCard is null) && !string.IsNullOrEmpty(value: word)) {
            var memberPosition = IsConstructMemberPosition(
                enclosing: enclosing,
                offset: offset,
                text: text,
                word: word
            );

            docCard = (PuckHoverInfo.Builtin(word: word)
                ?? GetDocumentationForWord(enclosing: enclosing, memberPosition: memberPosition, word: word)
                ?? GetStateHoverCard(parsedDocument: parsedDocument, uri: uri, word: word));
        }

        if (docCard is null) {
            SendResponse(
                id: id,
                result: null
            );
            return;
        }

        SendResponse(
            id: id,
            result: new JsonObject {
                ["contents"] = new JsonObject {
                    ["kind"] = "markdown",
                    ["value"] = docCard,
                },
            }
        );
    }
    private void HandleDefinition(JsonNode? id, JsonObject? @params) {
        var uri = (@params?["textDocument"]?["uri"]?.ToString() ?? "");
        var line = (@params?["position"]?["line"]?.GetValue<int>() ?? 0);
        var col = (@params?["position"]?["character"]?.GetValue<int>() ?? 0);

        if (!m_documents.TryGetValue(key: uri, value: out var text)) {
            SendResponse(id: id, result: null);
            return;
        }

        var sourcePath = (TryGetLocalPath(path: out var localPath, uri: uri) ? localPath : null);
        var vocabulary = Parsed(text: text).Vocabulary;
        var offset = LspJson.Offset(character: col, line: line, source: text);
        var parsedDocument = TryParseDocumentBestEffort(cursorOffset: offset, text: text);
        var target = PuckSymbolResolver.FindDefinition(
            character: col,
            document: parsedDocument,
            line: line,
            pathToUri: ToDocumentUri,
            readSource: ReadSourceText,
            source: text,
            sourcePath: sourcePath,
            sources: m_session.Sources,
            uri: uri,
            vocabulary: vocabulary
        );

        if (target is null) {
            SendResponse(id: id, result: null);
            return;
        }

        SendResponse(
            id: id,
            result: new JsonObject {
                ["uri"] = target.Uri,
                ["range"] = LspJson.Range(source: target.SourceText, span: target.Span),
            }
        );
    }
    private void HandleCodeAction(JsonNode? id, JsonObject? @params) {
        var uri = (@params?["textDocument"]?["uri"]?.ToString() ?? "");

        if (!m_documents.TryGetValue(key: uri, value: out var text)) {
            SendResponse(id: id, result: new JsonArray());
            return;
        }

        var actions = new JsonArray();
        var requestRange = (@params?["range"] as JsonObject);
        var diagnostics = new List<JsonObject>();

        if ((@params?["context"]?["diagnostics"] is JsonArray contextDiagnostics) && (contextDiagnostics.Count > 0)) {
            foreach (var entry in contextDiagnostics) {
                if (entry is JsonObject diagObj) {
                    diagnostics.Add(item: diagObj);
                }
            }
        } else if (m_publishedByTarget.TryGetValue(key: uri, value: out var published)) {
            foreach (var diagObj in published) {
                if ((requestRange is null) || RangesOverlap(a: requestRange, b: (diagObj["range"] as JsonObject))) {
                    diagnostics.Add(item: diagObj);
                }
            }
        }

        foreach (var diagnostic in diagnostics) {
            var code = diagnostic["code"]?.ToString();
            var message = (diagnostic["message"]?.ToString() ?? "");
            var range = (diagnostic["range"] as JsonObject);

            if ((code is null) || (range is null)) {
                continue;
            }

            if (code == PuckDiagnosticCodes.ColonBeforeContainer) {
                TryAddDropColonAction(actions: actions, diagnostic: diagnostic, range: range, text: text, uri: uri);
                continue;
            }

            if (code is PuckDiagnosticCodes.UnknownTemplate
                or PuckDiagnosticCodes.TemplateArgument
                or PuckDiagnosticCodes.UnknownMember
                or PuckDiagnosticCodes.LintUnresolvedState
                or PuckDiagnosticCodes.LintUnresolvedPrototype
                or PuckDiagnosticCodes.LintUnresolvedPlacementParent
                or PuckDiagnosticCodes.LintUnresolvedView
                or PuckDiagnosticCodes.UnresolvedParent) {
                TryAddNearestNameActions(actions: actions, code: code, diagnostic: diagnostic, message: message, range: range, text: text, uri: uri);
            }
        }

        SendResponse(id: id, result: actions);
    }
    private static void TryAddDropColonAction(JsonArray actions, JsonObject diagnostic, JsonObject range, string text, string uri) {
        if (!TryRangeOffsets(range: range, source: text, endOffset: out var endOffset, startOffset: out var startOffset)) {
            return;
        }

        var searchEnd = Math.Min(val1: text.Length, val2: (endOffset + 1));
        var sepIndex = -1;

        for (var index = Math.Max(val1: 0, val2: startOffset); (index < searchEnd); index++) {
            if (text[index] is ':' or '=') {
                sepIndex = index;
            }
        }

        if (sepIndex < 0) {
            return;
        }

        var sep = text[sepIndex];

        actions.Add(item: BuildQuickFix(
            diagnostic: diagnostic,
            editRange: LspJson.Range(
                end: LspJson.PositionOf(offset: (sepIndex + 1), source: text),
                start: LspJson.PositionOf(offset: sepIndex, source: text)
            ),
            isPreferred: true,
            newText: "",
            title: $"Drop the '{sep}' before container",
            uri: uri
        ));
    }
    private static void TryAddNearestNameActions(JsonArray actions, string code, JsonObject diagnostic, string message, JsonObject range, string text, string uri) {
        const string Marker = "; nearest: ";
        var markerIndex = message.IndexOf(comparisonType: StringComparison.Ordinal, value: Marker);

        if (markerIndex < 0) {
            return;
        }

        var written = ExtractWrittenName(code: code, message: message);

        if (string.IsNullOrEmpty(value: written)) {
            return;
        }

        var tail = message[(markerIndex + Marker.Length)..];
        var candidates = ExtractQuotedNames(text: tail);

        if (candidates.Count == 0) {
            return;
        }
        if (!TryRangeOffsets(range: range, source: text, endOffset: out var endOffset, startOffset: out var startOffset)) {
            return;
        }

        var sliceStart = Math.Clamp(max: text.Length, min: 0, value: startOffset);
        var sliceEnd = Math.Clamp(max: text.Length, min: sliceStart, value: endOffset);
        var matchIndex = FindNameInRange(name: written, sliceEnd: sliceEnd, sliceStart: sliceStart, text: text);

        if (matchIndex < 0) {
            return;
        }

        var editRange = LspJson.Range(
            end: LspJson.PositionOf(offset: (matchIndex + written.Length), source: text),
            start: LspJson.PositionOf(offset: matchIndex, source: text)
        );

        for (var index = 0; (index < candidates.Count); index++) {
            var candidate = candidates[index];

            actions.Add(item: BuildQuickFix(
                diagnostic: diagnostic,
                editRange: editRange,
                isPreferred: (index == 0),
                newText: candidate,
                title: $"Replace '{written}' with '{candidate}'",
                uri: uri
            ));
        }
    }
    private static string? ExtractWrittenName(string code, string message) {
        // For PUCK135 ("Template 'foo' has no parameter 'bar'; nearest: ..."), the written name is after "no parameter '".
        if (code == PuckDiagnosticCodes.TemplateArgument) {
            const string ParamMarker = "no parameter '";
            var idx = message.IndexOf(comparisonType: StringComparison.Ordinal, value: ParamMarker);

            if (idx >= 0) {
                var start = (idx + ParamMarker.Length);
                var close = message.IndexOf(startIndex: start, value: '\'');

                return ((close > start) ? message[start..close] : null);
            }
            return null;
        }

        var first = message.IndexOf(value: '\'');

        if (first < 0) {
            return null;
        }
        var second = message.IndexOf(startIndex: (first + 1), value: '\'');

        return ((second > (first + 1)) ? message[(first + 1)..second] : null);
    }
    private static List<string> ExtractQuotedNames(string text) {
        var names = new List<string>();
        var index = 0;

        while (index < text.Length) {
            var open = text.IndexOf(startIndex: index, value: '\'');

            if (open < 0) {
                break;
            }
            // Stop once past the nearest list (e.g. before "(parameters: ...)").
            if ((names.Count > 0) && text[index..open].Contains(value: '(')) {
                break;
            }
            var close = text.IndexOf(startIndex: (open + 1), value: '\'');

            if (close < 0) {
                break;
            }
            names.Add(item: text[(open + 1)..close]);
            index = (close + 1);
        }

        return names;
    }
    private static int FindNameInRange(string text, int sliceStart, int sliceEnd, string name) {
        var searchFrom = sliceStart;

        while (searchFrom < sliceEnd) {
            var found = text.IndexOf(
                comparisonType: StringComparison.Ordinal,
                count: (sliceEnd - searchFrom),
                startIndex: searchFrom,
                value: name
            );

            if (found < 0) {
                return -1;
            }

            var leftBoundary = ((found == 0) || !IdentifierSpelling.IsPart(character: text[(found - 1)]));
            var rightIndex = (found + name.Length);
            var rightBoundary = ((rightIndex >= text.Length) || !IdentifierSpelling.IsPart(character: text[rightIndex]));

            if (leftBoundary && rightBoundary) {
                return found;
            }
            searchFrom = (found + 1);
        }

        return -1;
    }
    private static JsonObject BuildQuickFix(string title, string uri, JsonObject editRange, string newText, JsonObject diagnostic, bool isPreferred) => new() {
        ["title"] = title,
        ["kind"] = "quickfix",
        ["isPreferred"] = isPreferred,
        ["diagnostics"] = new JsonArray(diagnostic.DeepClone()),
        ["edit"] = new JsonObject {
            ["changes"] = new JsonObject {
                [uri] = new JsonArray(new JsonObject {
                    ["range"] = editRange.DeepClone(),
                    ["newText"] = newText,
                }),
            },
        },
    };
    private static bool TryRangeOffsets(JsonObject range, string source, out int startOffset, out int endOffset) {
        startOffset = 0;
        endOffset = 0;
        if (
            (range["start"] is not JsonObject startObj) ||
            (range["end"] is not JsonObject endObj)
        ) {
            return false;
        }
        var startLine = (startObj["line"]?.GetValue<int>() ?? 0);
        var startChar = (startObj["character"]?.GetValue<int>() ?? 0);
        var endLine = (endObj["line"]?.GetValue<int>() ?? 0);
        var endChar = (endObj["character"]?.GetValue<int>() ?? 0);

        startOffset = LspJson.Offset(character: startChar, line: startLine, source: source);
        endOffset = LspJson.Offset(character: endChar, line: endLine, source: source);
        return (endOffset >= startOffset);
    }
    private static bool RangesOverlap(JsonObject a, JsonObject? b) {
        if (b is null) {
            return false;
        }
        var aStart = (Line: (a["start"]?["line"]?.GetValue<int>() ?? 0), Char: (a["start"]?["character"]?.GetValue<int>() ?? 0));
        var aEnd = (Line: (a["end"]?["line"]?.GetValue<int>() ?? 0), Char: (a["end"]?["character"]?.GetValue<int>() ?? 0));
        var bStart = (Line: (b["start"]?["line"]?.GetValue<int>() ?? 0), Char: (b["start"]?["character"]?.GetValue<int>() ?? 0));
        var bEnd = (Line: (b["end"]?["line"]?.GetValue<int>() ?? 0), Char: (b["end"]?["character"]?.GetValue<int>() ?? 0));

        return ((aStart.CompareTo(other: bEnd) <= 0) && (bStart.CompareTo(other: aEnd) <= 0));
    }
    private void HandleInitialize(JsonNode? id, JsonObject? @params) {
        // `initializationOptions.diagnostics`: "source" runs the source tier alone; anything else, or nothing, is full.
        m_depth = (((@params?["initializationOptions"]?["diagnostics"] is JsonValue depth) && depth.TryGetValue<string>(value: out var named) && (named == "source"))
            ? PuckDiagnosticDepth.Source
            : PuckDiagnosticDepth.Full
        );
        var capabilities = new JsonObject {
            ["capabilities"] = new JsonObject {
                ["textDocumentSync"] = 1, // Full
                ["completionProvider"] = new JsonObject {
                    ["resolveProvider"] = false,
                    ["triggerCharacters"] = new JsonArray(
            ((JsonNode)JsonValue.Create(".")!),
            ((JsonNode)JsonValue.Create(":")!),
            ((JsonNode)JsonValue.Create("$")!),
            ((JsonNode)JsonValue.Create("@")!),
            ((JsonNode)JsonValue.Create(" ")!),
            ((JsonNode)JsonValue.Create("\"")!),
            ((JsonNode)JsonValue.Create("#")!)
        ),
                },
                ["hoverProvider"] = true,
                ["definitionProvider"] = true,
                ["codeActionProvider"] = true,
                ["documentSymbolProvider"] = true,
                ["documentFormattingProvider"] = true,
                ["semanticTokensProvider"] = new JsonObject {
                    ["legend"] = new JsonObject {
                        ["tokenTypes"] = new JsonArray(items: [.. PuckSemanticTokens.TokenTypes.Select(selector: static name => ((JsonNode?)JsonValue.Create(value: name)))]),
                        ["tokenModifiers"] = new JsonArray(items: [.. PuckSemanticTokens.TokenModifiers.Select(selector: static name => ((JsonNode?)JsonValue.Create(value: name)))]),
                    },
                    ["full"] = true,
                },
            },
            ["serverInfo"] = new JsonObject {
                ["name"] = "Puck Language Server",
                ["version"] = "1.0.0",
            },
        };

        SendResponse(
            id: id,
            result: capabilities
        );
    }
    private void HandleMessage(JsonObject msg) {
        var id = msg["id"];
        var method = msg["method"]?.ToString();
        var @params = (msg["params"] as JsonObject);

        if (method is null) {
            return;
        }

        switch (method) {
            case "initialize":
                HandleInitialize(
                    id: id,
                    @params: @params
                );
                break;

            case "initialized":
                // Server ready notification
                break;

            case "textDocument/didOpen":
                if (@params?["textDocument"] is JsonObject openDoc) {
                    var uri = (openDoc["uri"]?.ToString() ?? "");
                    var text = (openDoc["text"]?.ToString() ?? "");

                    Edited(
                        text: text,
                        uri: uri,
                        version: ReadVersion(document: openDoc)
                    );
                }
                break;

            case "textDocument/didChange":
                if (
                    (@params?["textDocument"] is JsonObject changeDoc) &&
                    (@params["contentChanges"] is JsonArray changes)
                ) {
                    var uri = (changeDoc["uri"]?.ToString() ?? "");

                    if (
                        (changes.Count > 0) &&
                        (changes[^1] is JsonObject lastChange)
                    ) {
                        var text = (lastChange["text"]?.ToString() ?? "");

                        Edited(
                            text: text,
                            uri: uri,
                            version: ReadVersion(document: changeDoc)
                        );
                    }
                }
                break;

            case "textDocument/didClose":
                if (@params?["textDocument"] is JsonObject closeDoc) {
                    var uri = (closeDoc["uri"]?.ToString() ?? "");

                    Closed(uri: uri);
                }
                break;

            case "textDocument/completion":
                HandleCompletion(
                    id: id,
                    @params: @params
                );
                break;

            case "textDocument/hover":
                HandleHover(
                    id: id,
                    @params: @params
                );
                break;

            case "textDocument/definition":
                HandleDefinition(
                    id: id,
                    @params: @params
                );
                break;

            case "textDocument/codeAction":
                HandleCodeAction(
                    id: id,
                    @params: @params
                );
                break;

            case "textDocument/documentSymbol":
                HandleDocumentSymbol(
                    id: id,
                    @params: @params
                );
                break;

            case "textDocument/formatting":
                HandleFormatting(
                    id: id,
                    @params: @params
                );
                break;

            case "textDocument/semanticTokens/full":
                HandleSemanticTokens(
                    id: id,
                    @params: @params
                );
                break;

            case "shutdown":
                m_running = false;
                SendResponse(
                    id: id,
                    result: null
                );
                break;

            case "exit":
                m_running = false;
                break;

            default:
                // A request for a method the server does not offer is refused by name; an unknown notification is
                // ignored, as JSON-RPC requires.
                if (id is not null) {
                    SendError(
                        code: MethodNotFound,
                        id: id,
                        message: $"Method not found: {method}"
                    );
                }
                break;
        }
    }
    private void HandleSemanticTokens(JsonNode? id, JsonObject? @params) {
        var uri = (@params?["textDocument"]?["uri"]?.ToString() ?? "");
        var data = (m_documents.TryGetValue(
            key: uri,
            value: out var text
        )
            ? PuckSemanticTokens.Encode(
                source: text,
                tokens: PuckSemanticTokens.Classify(source: text)
            )
            : []
        );

        SendResponse(
            id: id,
            result: new JsonObject { ["data"] = new JsonArray(items: [.. data.Select(selector: static value => ((JsonNode?)JsonValue.Create(value: value)))]) }
        );
    }
    private void LogMessage(string message) => Send(message: LogNotification(message: message));
    private static JsonObject LogNotification(string message) => new() {
        ["jsonrpc"] = "2.0",
        ["method"] = "window/logMessage",
        ["params"] = new JsonObject {
            ["type"] = 4, // Info
            ["message"] = message,
        },
    };
    private void SendNotification(string method, JsonObject @params) {
        var notification = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params,
        };

        Send(message: notification);
    }
    // Every outgoing message leaves through the sink the message being handled brought with it.
    private void Send(JsonObject message) {
        if (m_send is not { } send) {
            throw new InvalidOperationException(message: "The language server sends only while it handles a message.");
        }

        send(obj: message);
    }
    // The JSON-RPC 2.0 error codes the server answers with.
    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InternalError = -32603;

    private void SendError(JsonNode? id, int code, string message) => Send(message: new JsonObject {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject {
            ["code"] = code,
            ["message"] = message,
        },
    });
    private void SendResponse(JsonNode? id, JsonNode? result) {
        var response = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };

        Send(message: response);
    }
    private static bool TryGetLocalPath(string uri, out string path) {
        if (
            Uri.TryCreate(
            result: out var parsed,
            uriKind: UriKind.Absolute,
            uriString: uri
        ) &&
            parsed.IsFile
        ) {
            path = Puck.Abstractions.PuckPaths.Slashed(path: parsed.LocalPath);
            // VS Code escapes the drive colon (file:///d%3A/...), which Uri.LocalPath treats as /d:/... .
            if (
                OperatingSystem.IsWindows() &&
                (path.Length >= 4) &&
                (path[0] == '/') &&
                char.IsAsciiLetter(c: path[1]) &&
                (path[2] == ':') &&
                (path[3] == '/')
            ) {
                path = path[1..];
            }
            return true;
        }
        path = "";
        return false;
    }

    /// <summary>Gets whether the session is still open: a <c>shutdown</c> request or an <c>exit</c> notification closes
    /// it, and a host stops handing it messages once it is closed.</summary>
    public bool IsRunning => m_running;

    /// <summary>Handles one JSON-RPC message, handing every message it writes in reply — responses, and notifications
    /// such as <c>textDocument/publishDiagnostics</c> — to <paramref name="send"/> in the order it writes them, before
    /// returning. A message that is not JSON is answered with a JSON-RPC parse error (-32700) and one that is not an
    /// object with an invalid-request error (-32600), both with a null id; a request for an unknown method is answered
    /// with method-not-found (-32601), and a request whose handling throws with an internal error (-32603) naming the
    /// failure, beside a <c>window/logMessage</c> notification. The session stays open.</summary>
    /// <param name="message">One JSON-RPC 2.0 message, as text.</param>
    /// <param name="send">Receives each outgoing message.</param>
    /// <remarks>This is the whole protocol; a transport only carries messages in and out of it. <see cref="RunAsync"/>
    /// is the stdio host (<c>puck lsp</c>), and the browser engine hands it one message per call. The server keeps its
    /// open documents between calls and is not safe for concurrent use.</remarks>
    /// <exception cref="InvalidOperationException">The server is already handling a message.</exception>
    public void Handle(string message, Action<JsonObject> send) {
        ArgumentNullException.ThrowIfNull(argument: message);
        ArgumentNullException.ThrowIfNull(argument: send);

        if (m_send is not null) {
            throw new InvalidOperationException(message: "The language server handles one message at a time.");
        }

        m_send = send;

        try {
            JsonNode? parsed;

            try {
                parsed = JsonNode.Parse(json: message);
            } catch (JsonException ex) {
                LogMessage(message: $"LSP parse error: {ex.Message}");
                // The id of a message that is not JSON cannot be read, so JSON-RPC answers it with a null id.
                SendError(code: ParseError, id: null, message: $"Parse error: {ex.Message}");

                return;
            }

            if (parsed is not JsonObject request) {
                SendError(code: InvalidRequest, id: null, message: "Invalid request: a JSON-RPC message is a JSON object.");

                return;
            }

            try {
                HandleMessage(msg: request);
            } catch (Exception ex) {
                var method = request["method"]?.ToString();

                LogMessage(message: $"LSP {method} failed: {ex.Message}");
                // A request is owed a response; a notification has no id to answer.
                if ((request["id"] is { } id) && (method is not null)) {
                    SendError(code: InternalError, id: id, message: $"{method} failed: {ex.Message}");
                }
            }
        } finally {
            m_send = null;
        }
    }
    /// <summary>Serves the session over the Language Server Protocol's base framing (<see cref="LspFraming"/>) until
    /// the session closes, <paramref name="input"/> ends, or <paramref name="input"/> breaks its framing.</summary>
    /// <param name="input">The stream to read framed messages from (e.g. <c>Console.OpenStandardInput()</c>).</param>
    /// <param name="output">The stream to write framed messages to (e.g. <c>Console.OpenStandardOutput()</c>).</param>
    /// <param name="cancellationToken">Ends the loop.</param>
    /// <returns>A task that completes when the loop ends and the diagnosis it started, if any, has returned.</returns>
    /// <remarks>Messages are read ahead of handling, and each is handled as soon as it is read. Pending diagnostic
    /// work runs, one document at a time, only while no read message waits, off the loop so a message arriving
    /// meanwhile is handled at once; a message that marks the document being diagnosed again cancels that diagnosis.
    /// When the input ends, the work still pending runs before the loop ends, since input that has ended is as quiet as
    /// input can be. When the session closes, a running diagnosis is cancelled and awaited, never abandoned.</remarks>
    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(argument: input);
        ArgumentNullException.ThrowIfNull(argument: output);

        var inbox = System.Threading.Channels.Channel.CreateUnbounded<string>();
        // The reader runs apart from the loop, so a read blocked on a quiet input never holds up diagnostic work, and is
        // left behind when the session closes before the input ends.
        _ = Task.Run(
            cancellationToken: cancellationToken,
            function: () => ReadAllAsync(
                cancellationToken: cancellationToken,
                inbox: inbox.Writer,
                input: input
            )
        );
        var outgoing = new List<JsonObject>();
        DiagnosisRequest? request = null;
        Task<Diagnosis>? diagnosing = null;
        CancellationTokenSource? cancelDiagnosis = null;

        async Task FlushAsync() {
            foreach (var reply in outgoing) {
                await LspFraming.WriteAsync(
                    cancellationToken: cancellationToken,
                    json: reply.ToJsonString(),
                    stream: output
                ).ConfigureAwait(continueOnCapturedContext: false);
            }
            outgoing.Clear();
        }

        try {
            while (
                m_running &&
                !cancellationToken.IsCancellationRequested
            ) {
                if (inbox.Reader.TryRead(item: out var message)) {
                    Handle(
                        message: message,
                        send: outgoing.Add
                    );
                    await FlushAsync().ConfigureAwait(continueOnCapturedContext: false);

                    if ((request is not null) && !IsCurrent(request: request)) {
                        await cancelDiagnosis!.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
                    }
                    continue;
                }
                if (diagnosing is not null) {
                    if (!diagnosing.IsCompleted) {
                        var waiting = inbox.Reader.WaitToReadAsync(cancellationToken: cancellationToken).AsTask();

                        if (
                            (await Task.WhenAny(
                                task1: diagnosing,
                                task2: waiting
                            ).ConfigureAwait(continueOnCapturedContext: false) == waiting) &&
                            !await waiting.ConfigureAwait(continueOnCapturedContext: false)
                        ) {
                            // The input has ended, so nothing can arrive to supersede the diagnosis.
                            await ((Task)diagnosing).ConfigureAwait(options: ConfigureAwaitOptions.SuppressThrowing);
                        }
                        continue;
                    }
                    try {
                        _ = CompleteDiagnosis(
                            diagnosis: await diagnosing.ConfigureAwait(continueOnCapturedContext: false),
                            send: outgoing.Add
                        );
                    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                        // Superseded: the newer mark is taken on a later quiet turn.
                    } catch (Exception exception) {
                        outgoing.Add(item: LogNotification(message: $"LSP diagnosis error: {exception.Message}"));
                    }
                    await FlushAsync().ConfigureAwait(continueOnCapturedContext: false);
                    cancelDiagnosis!.Dispose();
                    (request, diagnosing, cancelDiagnosis) = (null, null, null);
                    continue;
                }
                if (TakePendingDiagnosis() is { } next) {
                    var cancel = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);

                    (request, cancelDiagnosis) = (next, cancel);
                    diagnosing = Task.Run(
                        cancellationToken: cancel.Token,
                        function: () => Diagnose(
                            cancellationToken: cancel.Token,
                            request: next
                        )
                    );
                    continue;
                }
                if (!await inbox.Reader.WaitToReadAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false)) {
                    break;
                }
            }
        } finally {
            // The session ends only once the diagnosis it started has, so a host that exits when this returns never
            // cuts short work the diagnosis cannot abandon midway, such as a compile cache's write.
            if (cancelDiagnosis is not null) {
                await cancelDiagnosis.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
                await (((Task?)diagnosing) ?? Task.CompletedTask).ConfigureAwait(options: ConfigureAwaitOptions.SuppressThrowing);
                cancelDiagnosis.Dispose();
            }
        }
    }

    // Reads framed messages into the inbox until the input ends or breaks its framing; a stream that breaks its
    // framing cannot be resynchronized, so it ends the session as its end would.
    private static async Task ReadAllAsync(Stream input, System.Threading.Channels.ChannelWriter<string> inbox, CancellationToken cancellationToken) {
        try {
            while (await LspFraming.ReadAsync(
                cancellationToken: cancellationToken,
                stream: input
            ).ConfigureAwait(continueOnCapturedContext: false) is { } message) {
                _ = inbox.TryWrite(item: message);
            }
        } catch (InvalidDataException) {
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        } finally {
            _ = inbox.TryComplete();
        }
    }

    private readonly DocumentVocabularyResolver m_vocabularyResolver;
    private readonly WorldCompileSession m_session;

    /// <summary>Creates a language server session with no document open.</summary>
    /// <param name="diagnoseDocument">An optional schema dispatcher; returns true when it supplies the document's diagnostics.</param>
    /// <param name="completeDocument">An optional schema completion provider; null retains World completions.</param>
    /// <param name="vocabularyResolver">An optional vocabulary resolver; null uses default World resolver.</param>
    /// <param name="machines">The deployment's machine vocabulary, which the engine's validation of a world reads.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for composition under <paramref name="machines"/>.</param>
    /// <param name="session">The compile session every diagnosis, hover and completion compiles through;
    /// <see cref="WorldCompileSession.Shared"/> when omitted.</param>
    public PuckLanguageServer(Func<DocumentNode, string?, DiagnosticBag, bool>? diagnoseDocument = null,
        Func<DocumentNode, JsonArray?>? completeDocument = null,
        DocumentVocabularyResolver? vocabularyResolver = null,
        IMachineValidationCatalog? machines = null,
        string catalogFingerprint = "",
        WorldCompileSession? session = null) {
        m_session = (session ?? WorldCompileSession.Shared);
        m_catalogFingerprint = catalogFingerprint;
        m_machines = machines;
        m_diagnoseDocument = diagnoseDocument;
        m_completeDocument = completeDocument;
        m_vocabularyResolver = (vocabularyResolver ?? new DocumentVocabularyResolver(
            vocabularies: new Dictionary<string, IDocumentVocabulary>(),
            fallback: WorldDocumentVocabulary.Instance
        ));
    }
}
