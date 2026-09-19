using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler.Lsp;

/// <summary>High-performance, Native AOT-compatible Language Server Protocol (LSP) implementation for the Puck authoring language.</summary>
public sealed class PuckLanguageServer {
    private readonly string m_catalogFingerprint;
    private readonly Func<DocumentNode, JsonArray?>? m_completeDocument;
    private readonly Func<DocumentNode, string?, DiagnosticBag, bool>? m_diagnoseDocument;
    private readonly Stream m_input;
    private readonly IMachineValidationCatalog? m_machines;
    private readonly Stream m_output;

    private readonly Dictionary<string, string> m_documents = new(comparer: StringComparer.OrdinalIgnoreCase);
    private bool m_running = true;

    private static void AddCompletion(JsonArray items, string label, string insertText, string detail, int kind) {
        AddNode(
            array: items,
            node: new JsonObject {
                ["label"] = label,
                ["kind"] = kind,
                ["detail"] = detail,
                ["insertText"] = insertText,
                ["insertTextFormat"] = 2, // Snippet
            }
        );
    }
    private static void AddNode(JsonArray array, JsonNode? node) {
        array.Add(item: node);
    }
    private static JsonObject CreateRuleSymbol(RuleBlockNode rule) {
        var ruleSymbol = CreateSymbol(
            $"rule \"{rule.Name}\"",
            5,
            (rule.Line - 1),
            (rule.Column - 1),
            rule.Length
        );
        var children = new JsonArray();

        foreach (var stmt in rule.Statements) {
            switch (stmt) {
                case WhenStatementNode when1:
                    AddNode(
                        array: children,
                        node: CreateSymbol(
                            "when",
                            6,
                            (when1.Line - 1),
                            (when1.Column - 1),
                            when1.Length
                        )
                    );
                    break;
                case LocalStatementNode localStmt:
                    AddNode(
                        array: children,
                        node: CreateSymbol(
                            $"local {localStmt.Name}",
                            13,
                            (localStmt.Line - 1),
                            (localStmt.Column - 1),
                            localStmt.Length
                        )
                    );
                    break;
                case DecisionBlockNode decisionStmt:
                    AddNode(
                        array: children,
                        node: CreateSymbol(
                            "decision",
                            5,
                            (decisionStmt.Line - 1),
                            (decisionStmt.Column - 1),
                            decisionStmt.Length
                        )
                    );
                    break;
                case PropertyNode propStmt:
                    AddNode(
                        array: children,
                        node: CreateSymbol(
                            propStmt.Name,
                            7,
                            (propStmt.Line - 1),
                            (propStmt.Column - 1),
                            propStmt.Length
                        )
                    );
                    break;
            }
        }
        ruleSymbol["children"] = children;
        return ruleSymbol;
    }
    private static JsonObject CreateStateWorldSymbol(BlockNode world) {
        var symbol = CreateSymbol(
            "world",
            5,
            (world.Line - 1),
            (world.Column - 1),
            world.Length
        );
        var children = new JsonArray();

        foreach (var stmt in world.Statements) {
            switch (stmt) {
                case StateTableDeclarationNode table:
                    AddNode(
                        array: children,
                        node: CreateStateTableSymbol(table: table)
                    );
                    break;
                case StateSlotDeclarationNode slot:
                    AddNode(
                        array: children,
                        node: CreateSymbol(
                            $"slot {slot.Name} : {slot.Kind}",
                            8,
                            (slot.Line - 1),
                            (slot.Column - 1),
                            slot.Length
                        )
                    );
                    break;
                case BlockNode { Identifier: "row" } rowBlock:
                    AddNode(
                        array: children,
                        node: CreateSymbol(
                            "row",
                            8,
                            (rowBlock.Line - 1),
                            (rowBlock.Column - 1),
                            rowBlock.Length
                        )
                    );
                    break;
                case StatePileDeclarationNode pile:
                    AddNode(
                        array: children,
                        node: CreateStatePileSymbol(pile: pile)
                    );
                    break;
                case StateGridDeclarationNode grid:
                    AddNode(
                        array: children,
                        node: CreateSymbol(
                            $"grid {grid.Name} : {grid.Kind}",
                            8,
                            (grid.Line - 1),
                            (grid.Column - 1),
                            grid.Length
                        )
                    );
                    break;
            }
        }

        symbol["children"] = children;
        return symbol;
    }
    private static JsonObject CreateStatePileSymbol(StatePileDeclarationNode pile) {
        var symbol = CreateSymbol(
            $"pile {pile.Name} of {pile.TokenRow}",
            8,
            (pile.Line - 1),
            (pile.Column - 1),
            pile.Length
        );
        var children = new JsonArray();

        foreach (var token in pile.Tokens) {
            AddNode(
                array: children,
                node: CreateSymbol(
                    token.Key,
                    7,
                    (token.Line - 1),
                    (token.Column - 1),
                    token.Length
                )
            );
        }

        symbol["children"] = children;
        return symbol;
    }
    private static JsonObject CreateStateTableSymbol(StateTableDeclarationNode table) {
        var symbol = CreateSymbol(
            $"table {table.Name} : {table.Kind}",
            8,
            (table.Line - 1),
            (table.Column - 1),
            table.Length
        );
        var children = new JsonArray();

        foreach (var cell in table.Cells) {
            AddNode(
                array: children,
                node: CreateSymbol(
                    cell.Key,
                    7,
                    (cell.Line - 1),
                    (cell.Column - 1),
                    cell.Length
                )
            );
        }

        symbol["children"] = children;
        return symbol;
    }
    private static JsonObject CreateSymbol(string name, int kind, int line, int character, int length) {
        return new JsonObject {
            ["name"] = name,
            ["kind"] = kind,
            ["range"] = new JsonObject {
                ["start"] = new JsonObject { ["line"] = line, ["character"] = character },
                ["end"] = new JsonObject {
                    ["line"] = line,
                    ["character"] = (character + Math.Max(
            val1: 1,
            val2: length
        )),
                },
            },
            ["selectionRange"] = new JsonObject {
                ["start"] = new JsonObject { ["line"] = line, ["character"] = character },
                ["end"] = new JsonObject {
                    ["line"] = line,
                    ["character"] = (character + Math.Max(
            val1: 1,
            val2: length
        )),
                },
            },
        };
    }
    // The words this vocabulary's own construct table does not describe: the document headers, the compile-time
    // layer the core owns, the unit suffixes, and the two cell kinds no described member enumerates on its own.
    private static string? GetDocumentationForWord(string word, string? enclosing) => (WorldConstructLanguageServices.Hover(
        enclosing: enclosing,
        table: WorldConstructs.Table,
        word: word
    ) ?? (word switch {
        "schema" => "**`schema` Directive**\n\nDeclares the document schema family tag (e.g. `puck.world.definition.v1`, `puck.creation.v1`). Enables semantic validation and schema conformance checks.",
        "basis" => "**`basis` Directive**\n\nSpecifies the base world document path inherited by this world definition. Properties in this document override or compose over the basis.",
        "documentId" => "**`documentId` Directive**\n\nUnique string identifier for this world definition document.",
        "let" => "**`let` Declaration**\n\nDeclares a compile-time evaluated constant or alias (e.g. `let tickRate = 0.25s`, `let tableColor = #1b4d3e`).",
        "template" => "**`template` Definition**\n\nDeclares a reusable parametric template block expanded at compile time with default and named arguments.",
        "import" => "**`import` Declaration**\n\nImports symbols or components from another `.puck` or `.world.json` document.",
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
    // Best-effort: lowers the open document and looks `word` up as a declared `state` row's name, reporting its
    // kind. Swallows parse/lowering failures — a document mid-edit need not lower cleanly for hover to still work
    // on the parts that do.
    private string? GetStateRowHoverCard(string text, string word, string? sourcePath = null) {
        try {
            if (LowerStateSection(
                sourcePath: sourcePath,
                text: text
            ) is not { } stateSection) {
                return null;
            }
            foreach (var (_, section) in stateSection) {
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
    // A 0-based offset into `text` for an LSP line/character pair.
    private static int CursorOffset(string text, int line, int character) =>
        (text.Split('\n').Take(count: line).Sum(selector: static part => (part.Length + 1)) + character);
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

        var absolute = (lines.Take(count: targetLine).Sum(selector: part => (part.Length + 1)) + targetCol);

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
        if (!(char.IsLetterOrDigit(c: lineText[targetCol]) || (lineText[targetCol] is '_' or '$'))) {
            return null;
        }
        var start = targetCol;

        while (
            (start > 0) &&
            (char.IsLetterOrDigit(c: lineText[(start - 1)]) || (lineText[(start - 1)] == '_') || (lineText[(start - 1)] == '$'))
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
            (char.IsLetterOrDigit(c: lineText[end]) || (lineText[end] == '_') || (lineText[end] == '$'))
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
            (char.IsLetterOrDigit(c: lineText[(pos - 1)]) || (lineText[(pos - 1)] == '_'))
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
            (char.IsLetterOrDigit(c: lineText[(rowStart - 1)]) || (lineText[(rowStart - 1)] == '_'))
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
    // completion items — the dot-access counterpart to `GetStateRowHoverCard`'s row lookup. Returns null rather
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
                            AddCompletion(
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
    private async Task HandleCompletionAsync(JsonNode? id, JsonObject? @params) {
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
            await SendResponseAsync(
                id: id,
                result: new JsonObject { ["isIncomplete"] = false, ["items"] = specialized }
            ).ConfigureAwait(continueOnCapturedContext: false);
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
            var cursorOffset = (text.Split('\n').Take(count: line).Sum(selector: part => (part.Length + 1)) + col);
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
                await SendResponseAsync(
                    id: id,
                    result: new JsonObject { ["isIncomplete"] = false, ["items"] = keyItems }
                ).ConfigureAwait(continueOnCapturedContext: false);
                return;
            }
        }

        if (
            m_documents.TryGetValue(
            key: uri,
            value: out var docText
        )
        ) {
            var offset = (docText.Split('\n').Take(count: line).Sum(selector: part => (part.Length + 1)) + col);

            if (PuckSqlLsp.IsCursorInsideSqlBlock(
                cursorOffset: offset,
                resolver: m_vocabularyResolver,
                text: docText
            )) {
                var sqlItems = PuckSqlLsp.GetSqlCompletions(resolver: m_vocabularyResolver, text: docText);

                await SendResponseAsync(
                    id: id,
                    result: new JsonObject { ["isIncomplete"] = false, ["items"] = sqlItems }
                ).ConfigureAwait(continueOnCapturedContext: false);
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
                offset: CursorOffset(
                    character: col,
                    line: line,
                    text: completionText
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
        AddCompletion(
            detail: "Directive: Schema declaration",
            insertText: "schema: \"puck.world.definition.v1\"",
            items: items,
            kind: 14,
            label: "schema"
        );
        AddCompletion(
            detail: "Directive: Base world inheritance",
            insertText: "basis: \"worlds/base.puck\"",
            items: items,
            kind: 14,
            label: "basis"
        );
        AddCompletion(
            detail: "Directive: Document ID tag",
            insertText: "documentId: \"my-world-id\"",
            items: items,
            kind: 14,
            label: "documentId"
        );
        AddCompletion(
            detail: "Keyword: Declare constant",
            insertText: "let ${1:name} = ${2:value}",
            items: items,
            kind: 14,
            label: "let"
        );
        AddCompletion(
            detail: "Keyword: Parametric template",
            insertText: "template ${1:name}(${2:params}) {\n    $0\n}",
            items: items,
            kind: 14,
            label: "template"
        );
        AddCompletion(
            detail: "Keyword: Module import",
            insertText: "import \"${1:path}\" as ${2:alias}",
            items: items,
            kind: 14,
            label: "import"
        );
        AddCompletion(
            detail: "Keyword: Facet export",
            insertText: "export ${1|action,binding,read|} ${2:names}",
            items: items,
            kind: 14,
            label: "export"
        );

        // The array spelling of a row construct. A container takes no `:` — `materials: [` is PUCK040 — so the
        // inserted text is what the language accepts, which `ConstructCompletionSnippetLawTests` parses.
        AddCompletion(
            detail: "Section: Surface materials",
            insertText: "materials [\n    $0\n]",
            items: items,
            kind: 7,
            label: "materials"
        );
        AddCompletion(
            detail: "Section: Reactive state rules",
            insertText: "rules [\n    $0\n]",
            items: items,
            kind: 7,
            label: "rules"
        );
        AddCompletion(
            detail: "Section: WASM Addons",
            insertText: "addons [\n    $0\n]",
            items: items,
            kind: 7,
            label: "addons"
        );


        AddCompletion(
            detail: "Camera orbit operation",
            insertText: "orbit(pitch: ${1:0deg}, yaw: ${2:0deg}, distance: ${3:2.5m})",
            items: items,
            kind: 3,
            label: "orbit"
        );
        AddCompletion(
            detail: "Camera field-of-view operation",
            insertText: "fieldOfView(degrees: ${1:60})",
            items: items,
            kind: 3,
            label: "fieldOfView"
        );
        AddCompletion(
            detail: "Bitwise lattice shift function",
            insertText: "boardShift(${1:mask}, ${2:lattice}, ${3:dir})",
            items: items,
            kind: 3,
            label: "boardShift"
        );
        AddCompletion(
            detail: "Math clamp function",
            insertText: "clamp(${1:val}, ${2:min}, ${3:max})",
            items: items,
            kind: 3,
            label: "clamp"
        );

        AddCompletion(
            detail: "Unit: Seconds",
            insertText: "s",
            items: items,
            kind: 11,
            label: "s"
        );
        AddCompletion(
            detail: "Unit: Milliseconds",
            insertText: "ms",
            items: items,
            kind: 11,
            label: "ms"
        );
        AddCompletion(
            detail: "Unit: Hertz (frequency)",
            insertText: "hz",
            items: items,
            kind: 11,
            label: "hz"
        );
        AddCompletion(
            detail: "Unit: Radians",
            insertText: "rad",
            items: items,
            kind: 11,
            label: "rad"
        );
        AddCompletion(
            detail: "Unit: Degrees",
            insertText: "deg",
            items: items,
            kind: 11,
            label: "deg"
        );
        AddCompletion(
            detail: "Unit: Meters",
            insertText: "m",
            items: items,
            kind: 11,
            label: "m"
        );
        AddCompletion(
            detail: "Unit: Centimeters",
            insertText: "cm",
            items: items,
            kind: 11,
            label: "cm"
        );
        AddCompletion(
            detail: "Unit: Millimeters",
            insertText: "mm",
            items: items,
            kind: 11,
            label: "mm"
        );

        AddCompletion(
            detail: "Keyword: Rule/option gate",
            insertText: "when ${1:condition}",
            items: items,
            kind: 14,
            label: "when"
        );
        AddCompletion(
            detail: "Keyword: Gate conjunction",
            insertText: "and",
            items: items,
            kind: 14,
            label: "and"
        );
        AddCompletion(
            detail: "Keyword: Gate disjunction",
            insertText: "or",
            items: items,
            kind: 14,
            label: "or"
        );
        AddCompletion(
            detail: "Keyword: Gate negation",
            insertText: "not ${1:condition}",
            items: items,
            kind: 14,
            label: "not"
        );
        AddCompletion(
            detail: "Keyword: Comparison kind annotation — wrap in (...) beside and/or",
            insertText: "as ${1|Int,Fixed|}",
            items: items,
            kind: 14,
            label: "as"
        );
        AddCompletion(
            detail: "Keyword: 'if' alternate branch",
            insertText: "else {\n    $0\n}",
            items: items,
            kind: 14,
            label: "else"
        );

        AddCompletion(
            detail: "Predicate: compareState",
            insertText: "compareState(state: \"${1:row}\", comparison: ${2|Equal,NotEqual,Less,LessOrEqual,Greater,GreaterOrEqual|}, value: ${3:0})",
            items: items,
            kind: 3,
            label: "compareState"
        );
        AddCompletion(
            detail: "Predicate: compareValue",
            insertText: "compareValue(left: \"${1:expr}\", comparison: ${2|Equal,NotEqual,Less,LessOrEqual,Greater,GreaterOrEqual|}, right: \"${3:expr}\")",
            items: items,
            kind: 3,
            label: "compareValue"
        );
        AddCompletion(
            detail: "Effect: setState",
            insertText: "setState(state: \"${1:row}\", value: ${2:0})",
            items: items,
            kind: 3,
            label: "setState"
        );
        AddCompletion(
            detail: "Effect: addState",
            insertText: "addState(state: \"${1:row}\", value: ${2:0})",
            items: items,
            kind: 3,
            label: "addState"
        );
        AddCompletion(
            detail: "Effect: pushState",
            insertText: "pushState(state: \"${1:row}\", value: ${2:0})",
            items: items,
            kind: 3,
            label: "pushState"
        );
        AddCompletion(
            detail: "Effect: countdownState",
            insertText: "countdownState(state: \"${1:row}\")",
            items: items,
            kind: 3,
            label: "countdownState"
        );
        AddCompletion(
            detail: "Effect: removeStateCell",
            insertText: "removeStateCell(state: \"${1:row}\")",
            items: items,
            kind: 3,
            label: "removeStateCell"
        );
        AddCompletion(
            detail: "Effect: scheduleState",
            insertText: "scheduleState(state: \"${1:row}\", delaySeconds: ${2:1})",
            items: items,
            kind: 3,
            label: "scheduleState"
        );
        AddCompletion(
            detail: "CellKind: exact integer domain",
            insertText: "Int",
            items: items,
            kind: 13,
            label: "Int"
        );
        AddCompletion(
            detail: "CellKind: fixed-point domain",
            insertText: "Fixed",
            items: items,
            kind: 13,
            label: "Fixed"
        );
        AddCompletion(
            detail: "CellKind: boolean domain",
            insertText: "Bool",
            items: items,
            kind: 13,
            label: "Bool"
        );
        AddCompletion(
            detail: "CellKind: string domain",
            insertText: "Text",
            items: items,
            kind: 13,
            label: "Text"
        );

        await SendResponseAsync(
            id: id,
            result: new JsonObject {
                ["isIncomplete"] = false,
                ["items"] = items,
            }
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    private async Task HandleDocumentSymbolAsync(JsonNode? id, JsonObject? @params) {
        var uri = (@params?["textDocument"]?["uri"]?.ToString() ?? "");

        if (!m_documents.TryGetValue(
            key: uri,
            value: out var text
        )) {
            await SendResponseAsync(
                id: id,
                result: new JsonArray()
            ).ConfigureAwait(continueOnCapturedContext: false);
            return;
        }

        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            source: text,
            vocabulary: m_vocabularyResolver.Resolve(source: text)
        );
        var docNode = parseResult.Value;

        if (docNode is null) {
            await SendResponseAsync(
                id: id,
                result: new JsonArray()
            ).ConfigureAwait(continueOnCapturedContext: false);
            return;
        }

        var symbols = new JsonArray();

        foreach (var stmt in docNode.Statements) {
            if (stmt is BlockNode block) {
                var name = ((block.Name is not null)
                    ? $"{block.Identifier} \"{block.Name}\""
                    : block.Identifier
                );
                var blockSym = CreateSymbol(
                    name,
                    5,
                    (block.Line - 1),
                    (block.Column - 1),
                    block.Length
                );
                var children = new JsonArray();

                foreach (var child in block.Statements) {
                    if (
                        (child is BlockNode { Identifier: "world", Name: null, Target: null } worldBlock) &&
                        string.Equals(
                        a: block.Identifier,
                        b: "state",
                        comparisonType: StringComparison.OrdinalIgnoreCase
                    )
                    ) {
                        AddNode(
                            array: children,
                            node: CreateStateWorldSymbol(world: worldBlock)
                        );
                    } else if (
                        (child is BlockNode { Identifier: "spaces", Name: null, Target: null } spacesBlock) &&
                        string.Equals(
                        a: block.Identifier,
                        b: "state",
                        comparisonType: StringComparison.OrdinalIgnoreCase
                    )
                    ) {
                        AddNode(
                            array: children,
                            node: PuckEmbeddingLsp.CreateSpacesSymbol(spaces: spacesBlock)
                        );
                    } else if (child is BlockNode childBlock) {
                        var cName = ((childBlock.Name is not null)
                            ? $"{childBlock.Identifier} \"{childBlock.Name}\""
                            : childBlock.Identifier
                        );

                        AddNode(
                            array: children,
                            node: CreateSymbol(
                                cName,
                                5,
                                (childBlock.Line - 1),
                                (childBlock.Column - 1),
                                childBlock.Length
                            )
                        );
                    } else if (child is PropertyNode childProp) {
                        AddNode(
                            array: children,
                            node: CreateSymbol(
                                childProp.Name,
                                7,
                                (childProp.Line - 1),
                                (childProp.Column - 1),
                                childProp.Length
                            )
                        );
                    }
                }
                blockSym["children"] = children;
                AddNode(
                    array: symbols,
                    node: blockSym
                );
            } else if (stmt is PropertyNode prop) {
                AddNode(
                    array: symbols,
                    node: CreateSymbol(
                        prop.Name,
                        7,
                        (prop.Line - 1),
                        (prop.Column - 1),
                        prop.Length
                    )
                );
            } else if (stmt is LetNode letNode) {
                AddNode(
                    array: symbols,
                    node: CreateSymbol(
                        $"let {letNode.Name}",
                        13,
                        (letNode.Line - 1),
                        (letNode.Column - 1),
                        letNode.Length
                    )
                );
            } else if (stmt is TemplateNode tmpl) {
                AddNode(
                    array: symbols,
                    node: CreateSymbol(
                        $"template {tmpl.Name}",
                        11,
                        (tmpl.Line - 1),
                        (tmpl.Column - 1),
                        tmpl.Length
                    )
                );
            } else if (stmt is RuleBlockNode ruleBlock) {
                AddNode(
                    array: symbols,
                    node: CreateRuleSymbol(rule: ruleBlock)
                );
            }
        }

        await SendResponseAsync(
            id: id,
            result: symbols
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    private async Task HandleFormattingAsync(JsonNode? id, JsonObject? @params) {
        var uri = (@params?["textDocument"]?["uri"]?.ToString() ?? "");

        if (!m_documents.TryGetValue(
            key: uri,
            value: out var text
        )) {
            await SendResponseAsync(
                id: id,
                result: new JsonArray()
            ).ConfigureAwait(continueOnCapturedContext: false);
            return;
        }

        var options = @params?["options"];
        var tabSize = (options?["tabSize"]?.GetValue<int>() ?? 2);
        var insertSpaces = (options?["insertSpaces"]?.GetValue<bool>() ?? true);
        var printed = PuckPrinter.Format(
            options: new PuckPrintOptions {
                InsertSpaces = insertSpaces,
                TabSize = ((tabSize > 0)
                    ? tabSize
                    : 2),
            },
            source: text,
            vocabulary: m_vocabularyResolver.Resolve(source: text)
        );

        // A document that does not parse has no tree to print, so the editor keeps what the author is typing.
        if (printed.Value is not { } formatted) {
            await SendResponseAsync(
                id: id,
                result: new JsonArray()
            ).ConfigureAwait(continueOnCapturedContext: false);

            return;
        }

        var lines = text.Split('\n');
        var lastLine = Math.Max(
            val1: 0,
            val2: (lines.Length - 1)
        );
        var lastChar = ((lines.Length > 0)
            ? lines[^1].Length
            : 0
        );

        var edits = new JsonArray();

        AddNode(
            array: edits,
            node: new JsonObject {
                ["range"] = new JsonObject {
                    ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                    ["end"] = new JsonObject { ["line"] = lastLine, ["character"] = lastChar },
                },
                ["newText"] = formatted,
            }
        );

        await SendResponseAsync(
            id: id,
            result: edits
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    private async Task HandleHoverAsync(JsonNode? id, JsonObject? @params) {
        var uri = (@params?["textDocument"]?["uri"]?.ToString() ?? "");
        var line = (@params?["position"]?["line"]?.GetValue<int>() ?? 0);
        var col = (@params?["position"]?["character"]?.GetValue<int>() ?? 0);

        if (!m_documents.TryGetValue(
            key: uri,
            value: out var text
        )) {
            await SendResponseAsync(
                id: id,
                result: null
            ).ConfigureAwait(continueOnCapturedContext: false);
            return;
        }

        var word = GetWordAtPosition(
            targetCol: col,
            targetLine: line,
            text: text
        );

        var offset = CursorOffset(
            character: col,
            line: line,
            text: text
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
            await SendResponseAsync(
                id: id,
                result: new JsonObject {
                    ["contents"] = new JsonObject {
                        ["kind"] = "markdown",
                        ["value"] = embeddingCard,
                    },
                }
            ).ConfigureAwait(continueOnCapturedContext: false);
            return;
        }

        if (string.IsNullOrEmpty(value: word)) {
            await SendResponseAsync(
                id: id,
                result: null
            ).ConfigureAwait(continueOnCapturedContext: false);
            return;
        }

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
        ) ?? (PuckHoverInfo.Builtin(word: word) ?? (GetDocumentationForWord(enclosing: enclosing, word: word) ?? GetStateRowHoverCard(
            sourcePath: hoverSourcePath,
            text: text,
            word: word
        )))));

        if (docCard is null) {
            await SendResponseAsync(
                id: id,
                result: null
            ).ConfigureAwait(continueOnCapturedContext: false);
            return;
        }

        await SendResponseAsync(
            id: id,
            result: new JsonObject {
                ["contents"] = new JsonObject {
                    ["kind"] = "markdown",
                    ["value"] = docCard,
                },
            }
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    private async Task HandleInitializeAsync(JsonNode? id) {
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
            },
            ["serverInfo"] = new JsonObject {
                ["name"] = "Puck Language Server",
                ["version"] = "1.0.0",
            },
        };

        await SendResponseAsync(
            id: id,
            result: capabilities
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    private async Task HandleMessageAsync(JsonObject msg) {
        var id = msg["id"];
        var method = msg["method"]?.ToString();
        var @params = (msg["params"] as JsonObject);

        if (method is null) {
            return;
        }

        switch (method) {
            case "initialize":
                await HandleInitializeAsync(id: id).ConfigureAwait(continueOnCapturedContext: false);
                break;

            case "initialized":
                // Server ready notification
                break;

            case "textDocument/didOpen":
                if (@params?["textDocument"] is JsonObject openDoc) {
                    var uri = (openDoc["uri"]?.ToString() ?? "");
                    var text = (openDoc["text"]?.ToString() ?? "");

                    m_documents[uri] = text;
                    await PublishDiagnosticsAsync(
                        text: text,
                        uri: uri
                    ).ConfigureAwait(continueOnCapturedContext: false);
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

                        m_documents[uri] = text;
                        await PublishDiagnosticsAsync(
                            text: text,
                            uri: uri
                        ).ConfigureAwait(continueOnCapturedContext: false);
                    }
                }
                break;

            case "textDocument/didClose":
                if (@params?["textDocument"] is JsonObject closeDoc) {
                    var uri = (closeDoc["uri"]?.ToString() ?? "");

                    m_documents.Remove(key: uri);
                    await SendNotificationAsync(
                        method: "textDocument/publishDiagnostics",
                        @params: new JsonObject {
                            ["uri"] = uri,
                            ["diagnostics"] = new JsonArray(),
                        }
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }
                break;

            case "textDocument/completion":
                await HandleCompletionAsync(
                    id: id,
                    @params: @params
                ).ConfigureAwait(continueOnCapturedContext: false);
                break;

            case "textDocument/hover":
                await HandleHoverAsync(
                    id: id,
                    @params: @params
                ).ConfigureAwait(continueOnCapturedContext: false);
                break;

            case "textDocument/documentSymbol":
                await HandleDocumentSymbolAsync(
                    id: id,
                    @params: @params
                ).ConfigureAwait(continueOnCapturedContext: false);
                break;

            case "textDocument/formatting":
                await HandleFormattingAsync(
                    id: id,
                    @params: @params
                ).ConfigureAwait(continueOnCapturedContext: false);
                break;

            case "shutdown":
                m_running = false;
                await SendResponseAsync(
                    id: id,
                    result: null
                ).ConfigureAwait(continueOnCapturedContext: false);
                break;

            case "exit":
                m_running = false;
                break;

            default:
                if (id is not null) {
                    // Method not found
                    await SendResponseAsync(
                        id: id,
                        result: null
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }
                break;
        }
    }
    private async Task LogMessageAsync(string message) {
        await SendNotificationAsync(
            method: "window/logMessage",
            @params: new JsonObject {
                ["type"] = 4, // Info
                ["message"] = message,
            }
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    private async Task PublishDiagnosticsAsync(string uri, string text) {
        var diagnosticsBag = WorldSourceDiagnostics.Diagnose(
            catalogFingerprint: m_catalogFingerprint,
            foreign: m_diagnoseDocument,
            machines: m_machines,
            source: text,
            sourcePath: (TryGetLocalPath(
                path: out var sourcePath,
                uri: uri
            )
                ? sourcePath
                : null),
            vocabularies: m_vocabularyResolver
        );

        var lspDiags = new JsonArray();

        foreach (var diag in diagnosticsBag) {
            var severity = diag.Severity switch {
                DiagnosticSeverity.Error => 1,
                DiagnosticSeverity.Warning => 2,
                _ => 3
            };

            var startLine = Math.Max(
                val1: 0,
                val2: (diag.Span.Line - 1)
            );
            var startCol = Math.Max(
                val1: 0,
                val2: (diag.Span.Column - 1)
            );
            var endCol = (startCol + Math.Max(
                val1: 1,
                val2: diag.Span.Length
            ));

            AddNode(
                array: lspDiags,
                node: new JsonObject {
                    ["range"] = new JsonObject {
                        ["start"] = new JsonObject { ["line"] = startLine, ["character"] = startCol },
                        ["end"] = new JsonObject { ["line"] = startLine, ["character"] = endCol },
                    },
                    ["severity"] = severity,
                    ["code"] = diag.Code,
                    ["source"] = "puck",
                    ["message"] = diag.Message,
                }
            );
        }

        await SendNotificationAsync(
            method: "textDocument/publishDiagnostics",
            @params: new JsonObject {
                ["uri"] = uri,
                ["diagnostics"] = lspDiags,
            }
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken) {
        var contentLength = -1;
        var headerBuffer = new List<byte>();

        while (true) {
            var b = stream.ReadByte();

            if (b == -1) {
                return null;
            }

            headerBuffer.Add(item: ((byte)b));
            if (
                (headerBuffer.Count >= 4) &&
                (headerBuffer[^4] == '\r') &&
                (headerBuffer[^3] == '\n') &&
                (headerBuffer[^2] == '\r') &&
                (headerBuffer[^1] == '\n')
            ) {
                var headerText = Encoding.ASCII.GetString(bytes: headerBuffer.ToArray());

                foreach (var line in headerText.Split(
                    options: StringSplitOptions.RemoveEmptyEntries,
                    separator: ["\r\n"]
                )) {
                    if (line.StartsWith(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: "Content-Length:"
                    )) {
                        var lengthStr = line.Substring(startIndex: "Content-Length:".Length).Trim();

                        int.TryParse(
                            result: out contentLength,
                            s: lengthStr
                        );
                    }
                }
                break;
            }
        }

        if (contentLength <= 0) {
            return null;
        }

        var bodyBuffer = new byte[contentLength];
        var bytesRead = 0;

        while (bytesRead < contentLength) {
            var read = await stream.ReadAsync(
                buffer: bodyBuffer.AsMemory(
                    length: (contentLength - bytesRead),
                    start: bytesRead
                ),
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (read == 0) {
                return null;
            }
            bytesRead += read;
        }

        return Encoding.UTF8.GetString(bytes: bodyBuffer);
    }
    private async Task SendNotificationAsync(string method, JsonObject @params) {
        var notification = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params,
        };

        await WriteMessageAsync(
            m_output,
            notification.ToJsonString()
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    private async Task SendResponseAsync(JsonNode? id, JsonNode? result) {
        var response = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };

        await WriteMessageAsync(
            m_output,
            response.ToJsonString()
        ).ConfigureAwait(continueOnCapturedContext: false);
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
    private static async Task WriteMessageAsync(Stream stream, string json) {
        var bytes = Encoding.UTF8.GetBytes(s: json);
        var header = $"Content-Length: {bytes.Length}\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(s: header);

        await stream.WriteAsync(headerBytes).ConfigureAwait(continueOnCapturedContext: false);
        await stream.WriteAsync(bytes).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync().ConfigureAwait(continueOnCapturedContext: false);
    }

    /// <summary>Runs the Language Server loop until shutdown or EOF.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default) {
        while (
            m_running &&
            !cancellationToken.IsCancellationRequested
        ) {
            var message = await ReadMessageAsync(
                cancellationToken: cancellationToken,
                stream: m_input
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (message is null) {
                break;
            }

            try {
                var jsonNode = JsonNode.Parse(message);

                if (jsonNode is JsonObject requestObj) {
                    await HandleMessageAsync(msg: requestObj).ConfigureAwait(continueOnCapturedContext: false);
                }
            } catch (Exception ex) {
                // Log and continue
                await LogMessageAsync(message: $"LSP parse error: {ex.Message}").ConfigureAwait(continueOnCapturedContext: false);
            }
        }
    }

    private readonly DocumentVocabularyResolver m_vocabularyResolver;

    /// <summary>Creates a new instance of the Puck Language Server over the given input and output streams.</summary>
    /// <param name="input">The stream to read LSP JSON-RPC messages from (e.g. Console.OpenStandardInput()).</param>
    /// <param name="output">The stream to write LSP JSON-RPC messages to (e.g. Console.OpenStandardOutput()).</param>
    /// <param name="diagnoseDocument">An optional schema dispatcher; returns true when it supplies the document's diagnostics.</param>
    /// <param name="completeDocument">An optional schema completion provider; null retains World completions.</param>
    /// <param name="vocabularyResolver">An optional vocabulary resolver; null uses default World resolver.</param>
    /// <param name="machines">The deployment's machine vocabulary, which the engine's validation of a world reads.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for composition under <paramref name="machines"/>.</param>
    public PuckLanguageServer(Stream input, Stream output, Func<DocumentNode, string?, DiagnosticBag, bool>? diagnoseDocument = null,
        Func<DocumentNode, JsonArray?>? completeDocument = null,
        DocumentVocabularyResolver? vocabularyResolver = null,
        IMachineValidationCatalog? machines = null,
        string catalogFingerprint = "") {
        m_catalogFingerprint = catalogFingerprint;
        m_machines = machines;
        m_input = input;
        m_output = output;
        m_diagnoseDocument = diagnoseDocument;
        m_completeDocument = completeDocument;
        m_vocabularyResolver = (vocabularyResolver ?? new DocumentVocabularyResolver(
            vocabularies: new Dictionary<string, IDocumentVocabulary>(),
            fallback: WorldDocumentVocabulary.Instance
        ));
    }
}
