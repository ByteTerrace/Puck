using System.Text;
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
        "import" => "**`import` Declaration**\n\nLoads compile-time declarations from a `.puck` module, or composes a runtime world document named without a file extension (`import \"games/klondike\"`).",
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
    // Best-effort: lowers the open document and looks `word` up among its `state` declarations: an enum, reporting its
    // members, or a row, reporting its kind. Swallows parse/lowering failures — a document mid-edit need not lower
    // cleanly for hover to still work on the parts that do.
    private string? GetStateHoverCard(string text, string word, string? sourcePath = null) {
        try {
            if (LowerStateSection(
                sourcePath: sourcePath,
                text: text
            ) is not { } stateSection) {
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
                    // A row names its enum after `: ` as a record field does, so the name is hovered where it stands.
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
        } catch {
            return null;
        }
        return null;
    }
    private static string? GetWordAtPosition(string text, int targetLine, int targetCol) {
        var lines = text.Split('\n');

        if (
            (targetLine < 0) ||
            (targetLine >= lines.Length)
        ) {
            return null;
        }

        var lineText = lines[targetLine];

        if (
            (targetCol < 0) ||
            (targetCol >= lineText.Length)
        ) {
            return null;
        }

        var absolute = LspJson.Offset(character: targetCol, line: targetLine, source: text);

        for (var index = 0; (index <= absolute); ++index) {
            var lexicalEnd = SourceLexemes.End(
                offset: index,
                source: text
            );

            if (lexicalEnd > index) {
                if (absolute < lexicalEnd) {
                    return (((text[index] == '`') && (lexicalEnd > (index + 1)) && (text[(lexicalEnd - 1)] == '`'))
                        ? text.Substring(
                            length: ((lexicalEnd - index) - 2),
                            startIndex: (index + 1)
                        )
                        : null
                    );
                }
                index = (lexicalEnd - 1);
            }
        }
        if (!IdentifierSpelling.IsNameCharacter(character: lineText[targetCol])) {
            return null;
        }
        var start = targetCol;

        while (
            (start > 0) &&
            IdentifierSpelling.IsNameCharacter(character: lineText[(start - 1)])
        ) {
            start--;
        }

        if (
            (start > 0) &&
            (lineText[(start - 1)] == '.')
        ) {
            return null;
        }
        var end = targetCol;

        while (
            (end < lineText.Length) &&
            IdentifierSpelling.IsNameCharacter(character: lineText[end])
        ) {
            end++;
        }

        return ((start < end)
            ? lineText.Substring(
                length: (end - start),
                startIndex: start
            )
            : null
        );
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
    // Best-effort, recovery-tolerant parse for a buffer mid-edit: a clean parse covers the common case (the
    // dotted read sits inside an already-closed rule), and the fallback truncates at the cursor and synthesizes
    // the closing braces/brackets/parens the truncated prefix is still owed, so an unclosed rule or block being
    // typed for the first time still parses far enough to see its declared rows.
    private DocumentNode? TryParseDocumentBestEffort(string text, int cursorOffset) {
        try {
            if (PuckParser.ParseDocumentWithDiagnostics(source: text, vocabulary: m_vocabularyResolver.Resolve(source: text)).Value is { } direct) {
                return direct;
            }
        } catch {
            // Falls through to the truncate-and-close recovery below.
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
    // The `state` section `text` lowers to, or null when the text is not a world source or lowers to none. Whatever
    // the compile reports is dropped: a document mid-edit still answers for the rows that do lower.
    private JsonObject? LowerStateSection(string text, string? sourcePath) {
        if (m_vocabularyResolver.Resolve(source: text) is not WorldDocumentVocabulary vocabulary) {
            return null;
        }

        return (WorldCompiler.Compile(
            imports: ImportHandling.Ignore,
            source: text,
            sourcePath: sourcePath,
            vocabulary: vocabulary
        ).Json?["state"] as JsonObject);
    }
    // The closing `}`/`]`/`)` sequence, innermost first, that balances every delimiter `source` opened and never
    // closed — skipping string/backquote contents and comments, exactly like the formatter's own nesting scan.
    private static string ComputeClosingSuffix(string source) {
        var stack = new Stack<char>();
        var i = 0;

        while (i < source.Length) {
            var c = source[i];

            if (c is '"' or '`') {
                var quote = c;

                i++;
                while (
                    (i < source.Length) &&
                    (source[i] != quote)
                ) {
                    if (
                        (quote == '"') &&
                        (source[i] == '\\') &&
                        ((i + 1) < source.Length)
                    ) {
                        i++;
                    }
                    i++;
                }
                i++;
                continue;
            }
            if (
                (c == '/') &&
                ((i + 1) < source.Length) &&
                (source[(i + 1)] == '/')
            ) {
                while (
                    (i < source.Length) &&
                    (source[i] != '\n')
                ) {
                    i++;
                }
                continue;
            }
            if (
                (c == '/') &&
                ((i + 1) < source.Length) &&
                (source[(i + 1)] == '*')
            ) {
                i += 2;
                while (
                    ((i + 1) < source.Length) &&
                    !((source[i] == '*') && (source[(i + 1)] == '/'))
                ) {
                    i++;
                }
                i += 2;
                continue;
            }

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
    // completion items — the dot-access counterpart to `GetStateHoverCard`'s row lookup. Returns null rather
    // than an empty array when the row can't be found or carries no cells, so the caller falls back to the
    // generic keyword list instead of offering zero completions for what might just be an unresolved recovery.
    private JsonArray? GetStateRowKeyCompletions(string text, string rowName, int cursorOffset, string? sourcePath = null) {
        try {
            if ((LowerStateSection(
                sourcePath: sourcePath,
                text: text
            ) ?? LowerStateSection(
                sourcePath: sourcePath,
                text: RecoverToCursor(
                    cursorOffset: cursorOffset,
                    text: text
                )
            )) is not { } stateSection) {
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
    private void HandleCompletion(JsonNode? id, JsonObject? @params) {
        var uri = (@params?["textDocument"]?["uri"]?.ToString() ?? "");

        if (
            (m_completeDocument is not null) &&
            m_documents.TryGetValue(
            key: uri,
            value: out var source
        ) &&
            (PuckParser.ParseDocumentWithDiagnostics(source: source, vocabulary: m_vocabularyResolver.Resolve(source: source)).Value is { } document) &&
            (m_completeDocument(document) is { } specialized)
        ) {
            SendResponse(
                id: id,
                result: new JsonObject { ["isIncomplete"] = false, ["items"] = specialized }
            );
            return;
        }

        // A dot-access read (`vitals.`) completes the declared table's own cell keys instead of the generic
        // keyword list — the same grammar `ExpressionSpelling` reads `row.key` through (stage 3). The cursor's
        // line/column, not its dotted-access AST, decides the row name: the surrounding statement is very often
        // still incomplete while this fires (trailing dot, unclosed rule), so `TryGetDotAccessRowName` reads raw
        // text and `GetStateRowKeyCompletions` reparses with recovery rather than trusting a clean parse.
        var line = (((int?)@params?["position"]?["line"]) ?? 0);
        var col = (((int?)@params?["position"]?["character"]) ?? 0);

        if (
            m_documents.TryGetValue(
            key: uri,
            value: out var text
        ) &&
            TryGetDotAccessRowName(
            rowName: out var dotRowName,
            targetCol: col,
            targetLine: line,
            text: text
        )
        ) {
            var cursorOffset = LspJson.Offset(character: col, line: line, source: text);
            string? completionSourcePath = null;

            if (TryGetLocalPath(path: out var localCompPath, uri: uri)) {
                completionSourcePath = localCompPath;
            }

            var keyItems = (GetStateRowKeyCompletions(
                cursorOffset: cursorOffset,
                rowName: dotRowName,
                sourcePath: completionSourcePath,
                text: text
            ) ?? PuckSqlLsp.GetSqlTableColumnCompletions(
                resolver: m_vocabularyResolver,
                tableName: dotRowName,
                text: text
            ));

            if (keyItems is { Count: > 0 }) {
                SendResponse(
                    id: id,
                    result: new JsonObject { ["isIncomplete"] = false, ["items"] = keyItems }
                );
                return;
            }
        }

        if (
            m_documents.TryGetValue(
            key: uri,
            value: out var docText
        )
        ) {
            var offset = LspJson.Offset(character: col, line: line, source: docText);

            if (PuckSqlLsp.IsCursorInsideSqlBlock(
                cursorOffset: offset,
                resolver: m_vocabularyResolver,
                text: docText
            )) {
                var sqlItems = PuckSqlLsp.GetSqlCompletions(resolver: m_vocabularyResolver, text: docText);

                SendResponse(
                    id: id,
                    result: new JsonObject { ["isIncomplete"] = false, ["items"] = sqlItems }
                );
                return;
            }
        }

        var items = new JsonArray();

        var completionText = (m_documents.TryGetValue(
            key: uri,
            value: out var openText
        )
            ? openText
            : ""
        );

        PuckEmbeddingLsp.AddCompletions(items: items);
        WorldConstructLanguageServices.AddCompletions(
            enclosing: WorldConstructLanguageServices.ConstructAt(
                offset: LspJson.Offset(
                    character: col,
                    line: line,
                    source: completionText
                ),
                table: WorldConstructs.Table,
                text: completionText
            ),
            items: items,
            table: WorldConstructs.Table
        );

        // What follows is everything the construct table does not describe: the document headers, the
        // compile-time layer the core owns, the camera and scalar function forms, the unit suffixes, the
        // plural array properties, and the `$type` call-form escape hatches.
        LspJson.AddCompletion(
            detail: "Directive: Schema declaration",
            insertText: "schema: \"puck.world.definition.v1\"",
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
            detail: "Keyword: Declare constant",
            insertText: "let ${1:name} = ${2:value}",
            items: items,
            kind: 14,
            label: "let"
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
            detail: "Keyword: Module import",
            insertText: "import \"${1:path}\" as ${2:alias}",
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

        // The array spelling of a row construct. A container takes no `:` — `materials: [` is PUCK040 — so the
        // inserted text is what the language accepts, which `ConstructCompletionSnippetLawTests` parses.
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

        LspJson.AddCompletion(
            detail: "Unit: Seconds",
            insertText: "s",
            items: items,
            kind: 11,
            label: "s"
        );
        LspJson.AddCompletion(
            detail: "Unit: Milliseconds",
            insertText: "ms",
            items: items,
            kind: 11,
            label: "ms"
        );
        LspJson.AddCompletion(
            detail: "Unit: Hertz (frequency)",
            insertText: "hz",
            items: items,
            kind: 11,
            label: "hz"
        );
        LspJson.AddCompletion(
            detail: "Unit: Radians",
            insertText: "rad",
            items: items,
            kind: 11,
            label: "rad"
        );
        LspJson.AddCompletion(
            detail: "Unit: Degrees",
            insertText: "deg",
            items: items,
            kind: 11,
            label: "deg"
        );
        LspJson.AddCompletion(
            detail: "Unit: Meters",
            insertText: "m",
            items: items,
            kind: 11,
            label: "m"
        );
        LspJson.AddCompletion(
            detail: "Unit: Centimeters",
            insertText: "cm",
            items: items,
            kind: 11,
            label: "cm"
        );
        LspJson.AddCompletion(
            detail: "Unit: Millimeters",
            insertText: "mm",
            items: items,
            kind: 11,
            label: "mm"
        );

        LspJson.AddCompletion(
            detail: "Keyword: Rule/option gate",
            insertText: "when ${1:condition}",
            items: items,
            kind: 14,
            label: "when"
        );
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
        LspJson.AddCompletion(
            detail: "Keyword: Comparison kind annotation — wrap in (...) beside and/or",
            insertText: "as ${1|Int,Fixed|}",
            items: items,
            kind: 14,
            label: "as"
        );
        LspJson.AddCompletion(
            detail: "Keyword: 'if' alternate branch",
            insertText: "else {\n    $0\n}",
            items: items,
            kind: 14,
            label: "else"
        );

        LspJson.AddCompletion(
            detail: "Predicate: compareState",
            insertText: "compareState(state: \"${1:row}\", comparison: ${2|Equal,NotEqual,Less,LessOrEqual,Greater,GreaterOrEqual|}, value: ${3:0})",
            items: items,
            kind: 3,
            label: "compareState"
        );
        LspJson.AddCompletion(
            detail: "Predicate: compareValue",
            insertText: "compareValue(left: \"${1:expr}\", comparison: ${2|Equal,NotEqual,Less,LessOrEqual,Greater,GreaterOrEqual|}, right: \"${3:expr}\")",
            items: items,
            kind: 3,
            label: "compareValue"
        );
        LspJson.AddCompletion(
            detail: "Effect: setState",
            insertText: "setState(state: \"${1:row}\", value: ${2:0})",
            items: items,
            kind: 3,
            label: "setState"
        );
        LspJson.AddCompletion(
            detail: "Effect: addState",
            insertText: "addState(state: \"${1:row}\", value: ${2:0})",
            items: items,
            kind: 3,
            label: "addState"
        );
        LspJson.AddCompletion(
            detail: "Effect: pushState",
            insertText: "pushState(state: \"${1:row}\", value: ${2:0})",
            items: items,
            kind: 3,
            label: "pushState"
        );
        LspJson.AddCompletion(
            detail: "Effect: removeStateCell",
            insertText: "removeStateCell(state: \"${1:row}\")",
            items: items,
            kind: 3,
            label: "removeStateCell"
        );
        LspJson.AddCompletion(
            detail: "Effect: scheduleState",
            insertText: "scheduleState(state: \"${1:row}\", delaySeconds: ${2:1})",
            items: items,
            kind: 3,
            label: "scheduleState"
        );
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

        SendResponse(
            id: id,
            result: new JsonObject {
                ["isIncomplete"] = false,
                ["items"] = items,
            }
        );
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

        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            source: text,
            vocabulary: m_vocabularyResolver.Resolve(source: text)
        );
        var docNode = parseResult.Value;

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
        if (printed.Value is not { } formatted) {
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

        var word = GetWordAtPosition(
            targetCol: col,
            targetLine: line,
            text: text
        );

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

        if (string.IsNullOrEmpty(value: word)) {
            SendResponse(
                id: id,
                result: null
            );
            return;
        }

        var memberPosition = IsConstructMemberPosition(
            enclosing: enclosing,
            offset: offset,
            text: text,
            word: word
        );
        var docCard = (PuckSqlLsp.GetSqlHoverCard(
            offset: offset,
            resolver: m_vocabularyResolver,
            text: text,
            word: word
        ) ?? (PuckHoverInfo.Declaration(
            offset: offset,
            resolver: m_vocabularyResolver,
            source: text,
            word: word
        ) ?? (PuckHoverInfo.Builtin(word: word) ?? (GetDocumentationForWord(enclosing: enclosing, memberPosition: memberPosition, word: word) ?? GetStateHoverCard(
            sourcePath: hoverSourcePath,
            text: text,
            word: word
        )))));

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

                    _ = m_documents.Remove(key: uri);
                    _ = m_versions.Remove(key: uri);
                    _ = m_generations.Remove(key: uri);
                    _ = m_pending.Remove(key: uri);
                    _ = m_dependencies.Remove(key: uri);
                    _ = m_semanticPending.Remove(key: uri);
                    SendNotification(
                        method: "textDocument/publishDiagnostics",
                        @params: new JsonObject {
                            ["uri"] = uri,
                            ["diagnostics"] = new JsonArray(),
                        }
                    );
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
                if (id is not null) {
                    // Method not found
                    SendResponse(
                        id: id,
                        result: null
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
                tokens: PuckSemanticTokens.Classify(
                    source: text,
                    vocabulary: m_vocabularyResolver.Resolve(source: text)
                )
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
            path = parsed.LocalPath.Replace(
                newChar: '/',
                oldChar: '\\'
            );
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
    /// returning. A message that is not JSON, or whose handling throws, is answered with a <c>window/logMessage</c>
    /// notification naming the failure, and the session stays open.</summary>
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
            try {
                if (JsonNode.Parse(json: message) is JsonObject request) {
                    HandleMessage(msg: request);
                }
            } catch (Exception ex) {
                LogMessage(message: $"LSP parse error: {ex.Message}");
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

    /// <summary>Creates a language server session with no document open.</summary>
    /// <param name="diagnoseDocument">An optional schema dispatcher; returns true when it supplies the document's diagnostics.</param>
    /// <param name="completeDocument">An optional schema completion provider; null retains World completions.</param>
    /// <param name="vocabularyResolver">An optional vocabulary resolver; null uses default World resolver.</param>
    /// <param name="machines">The deployment's machine vocabulary, which the engine's validation of a world reads.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for composition under <paramref name="machines"/>.</param>
    public PuckLanguageServer(Func<DocumentNode, string?, DiagnosticBag, bool>? diagnoseDocument = null,
        Func<DocumentNode, JsonArray?>? completeDocument = null,
        DocumentVocabularyResolver? vocabularyResolver = null,
        IMachineValidationCatalog? machines = null,
        string catalogFingerprint = "") {
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
