using System.Text;
using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;

namespace Puck.World.Transpiler.Lsp;

/// <summary>High-performance, Native AOT-compatible Language Server Protocol (LSP) implementation for the Puck authoring language.</summary>
public sealed class PuckLanguageServer {
    private readonly Func<DocumentNode, JsonArray?>? m_completeDocument;
    private readonly Func<DocumentNode, string?, DiagnosticBag, bool>? m_diagnoseDocument;
    private readonly Stream m_input;
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
                case BindStatementNode bindStmt:
                    AddNode(
                        array: children,
                        node: CreateSymbol(
                            $"bind {bindStmt.Name}",
                            13,
                            (bindStmt.Line - 1),
                            (bindStmt.Column - 1),
                            bindStmt.Length
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
                ["end"] = new JsonObject { ["line"] = line, ["character"] = (character + Math.Max(
            val1: 1,
            val2: length
        )) },
            },
            ["selectionRange"] = new JsonObject {
                ["start"] = new JsonObject { ["line"] = line, ["character"] = character },
                ["end"] = new JsonObject { ["line"] = line, ["character"] = (character + Math.Max(
            val1: 1,
            val2: length
        )) },
            },
        };
    }
    private static string? GetDocumentationForWord(string word) => word switch {
        "schema" => "**`schema` Directive**\n\nDeclares the document schema family tag (e.g. `puck.world.definition.v1`, `puck.creation.v1`). Enables semantic validation and schema conformance checks.",
        "basis" => "**`basis` Directive**\n\nSpecifies the base world document path inherited by this world definition. Properties in this document override or compose over the basis.",
        "documentId" => "**`documentId` Directive**\n\nUnique string identifier for this world definition document.",
        "let" => "**`let` Declaration**\n\nDeclares a compile-time evaluated constant or alias (e.g. `let tickRate = 0.25s`, `let tableColor = #1b4d3e`).",
        "template" => "**`template` Definition**\n\nDeclares a reusable parametric template block expanded at compile time with default and named arguments.",
        "import" => "**`import` Declaration**\n\nImports symbols or components from another `.puck` or `.world.json` document.",
        "export" => "**`export` Declaration**\n\nDeclares exported world facets (`action`, `binding`, `read`) exposed across the network and to client sessions.",
        "host" => "**`host` Section**\n\nConfigures the application host window, display dimensions, presentation mode (`offscreen`, `windowed`), and simulation tick rate.",
        "views" => "**`views` Section**\n\nConfigures camera layouts, viewports, and seat rigs.",
        "seatRig" => "**`seatRig` Section**\n\nDeclares a camera seat rig containing scheduled camera operations such as `orbit` and `fov`.",
        "orbit" => "**`orbit(pitch:, yaw:, distance:)`**\n\nConfigures spherical orbit camera positioning relative to the focus target.",
        "fieldOfView" => "**`fieldOfView(degrees:)`**\n\nSets the camera vertical field-of-view in degrees.",
        "solids" => "**`solids` Section**\n\nCollection of Signed Distance Field (SDF) Constructive Solid Geometry (CSG) primitives evaluated by the raymarching engine.",
        "materials" => "**`materials` Section**\n\nSurface material properties including albedo color, roughness, metallic, and reflectance.",
        "state" => "**`state` Section**\n\nWorld state definitions including discrete values, lattices, and cell arrays.",
        "table" => "**`table name : Kind [capacity(n)] [bounds(...)] [advance(perSecond:)] { key = value ... }`**\n\nDeclares a keyed `state.world` row — sugar for the explicit `cells` array. Legal only directly inside `state.world`.",
        "slot" => "**`slot name : Kind [= value] [bounds(...)] [advance(perSecond:)]`**\n\nDeclares a scalar `state.world` row — sugar for the explicit `value` field. Legal only directly inside `state.world`.",
        "bounds" => "**`bounds(minimum:, maximum:, overflow:)`**\n\nDeclares a table/slot row's range and overflow policy (`Refuse`, the default, or `Saturate`). Every argument is optional; only legal on an `Int`/`Fixed` row.",
        "capacity" => "**`capacity(n)`**\n\nDeclares a `table` row's cell-count ceiling. Refused smaller than the table's own authored cells.",
        "behavior" => "**`behavior(none)`**\n\nOpts a table cell out of its row's default `advance` behavior. The only admitted argument is `none`.",
        "advance" => "**`advance(perSecond:)`**\n\nDeclares a table/slot row's (or table cell's) per-second continuous accumulation rate, evaluated on engine ticks. Only legal on an `Int`/`Fixed` row.",
        "pile" => "**`pile name of tokenRow [capacity(n)] { token ... }`**\n\nDeclares an ordered-membership `state.world` row over `tokenRow`'s keys — sugar for a `keysOf` domain with `ordered` set. Legal only directly inside `state.world`.",
        "grid" => "**`grid name : Kind dimensions(width:, depth:) [wrap(...)] [cellSize(...)] [origin(...)] [band(...)] [empty(...)] [positions(...)] [inverse(tokens:, codes:)] [bounds(...)] [{ key = value ... }]`**\n\nDeclares a physical-lattice occupancy row — mints a `state.lattices` Grid topology of the same name and a `cellsOf` row over it. Legal only directly inside `state.world`.",
        "dimensions" => "**`dimensions(width:, depth:)`**\n\nA `grid`'s cell counts along +X and +Z. Required.",
        "wrap" => "**`wrap(None|X|Y|Both)`**\n\nA `grid`'s wrapped axes. Defaults to `None`.",
        "cellSize" => "**`cellSize(n)`**\n\nA `grid`'s cubic cell edge, in world units. Defaults to `1`.",
        "origin" => "**`origin(x, y, z)`**\n\nA `grid`'s minimum corner, in world units. Defaults to `(0, 0, 0)`.",
        "band" => "**`band(n)`**\n\nA `grid`'s vertical half-extent a position must lie within to resolve to a cell. Defaults to `0` (any height).",
        "empty" => "**`empty(value)`**\n\nThe value an unwritten `grid` cell reads. Defaults to `0`/`false`.",
        "positions" => "**`positions(tokenRow)`**\n\nMarks another row's integer values as cell ordinals of this `grid`'s own topology — sets that row's `valuesFrom`.",
        "inverse" => "**`inverse(tokens:, codes:)`**\n\nDeclares this `grid` a board derived from a token row's current cells and a codes row, rather than authored directly. Refused together with an authored cell body.",
        "Bool" => "**CellKind: `Bool`**\n\nA 0/1 boolean cell.",
        "Text" => "**CellKind: `Text`**\n\nA UTF-16 string cell.",
        "rules" => "**`rules` Section**\n\nDeclarative reactive rules evaluated on each engine tick (`effects`, `gate`, `mode`).",
        "addons" => "**`addons` Section**\n\nConfigures WebAssembly (WASM) game extensions with capability requests and memory watches.",
        "boardShift" => "**`boardShift(mask, lattice, direction)`**\n\nPerforms a directional bitwise shift across a multi-dimensional state lattice.",
        "deg" => "**`deg` Unit**\n\nAngular unit in degrees (automatically converted to radians: `value * PI / 180`).",
        "rad" => "**`rad` Unit**\n\nAngular unit in radians.",
        "s" => "**`s` Unit**\n\nTime duration unit in seconds.",
        "ms" => "**`ms` Unit**\n\nTime duration unit in milliseconds.",
        "hz" => "**`hz` Unit**\n\nFrequency unit in Hertz (e.g. `60hz`, `120hz`).",
        "m" => "**`m` Unit**\n\nSpatial metric unit in meters.",
        _ => null
    };
    // Best-effort: lowers the open document and looks `word` up as a declared `state` row's name, reporting its
    // kind. Swallows parse/lowering failures — a document mid-edit need not lower cleanly for hover to still work
    // on the parts that do.
    private static string? GetStateRowHoverCard(string text, string word) {
        try {
            var parseResult = PuckParser.ParseDocumentWithDiagnostics(text);

            if (parseResult.Value is not { } document) {
                return null;
            }
            if (WorldDocumentEmitter.LowerWithDiagnostics(document).Value?["state"] is not JsonObject stateSection) {
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
    private static DocumentNode? TryParseDocumentBestEffort(string text, int cursorOffset) {
        try {
            if (PuckParser.ParseDocumentWithDiagnostics(text).Value is { } direct) {
                return direct;
            }
        } catch {
            // Falls through to the truncate-and-close recovery below.
        }

        try {
            var prefix = text[..Math.Clamp(
                max: text.Length,
                min: 0,
                value: cursorOffset
            )];
            var recovered = (prefix + ComputeClosingSuffix(source: prefix));

            return PuckParser.ParseDocumentWithDiagnostics(recovered).Value;
        } catch {
            return null;
        }
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
    private static JsonArray? GetStateRowKeyCompletions(string text, string rowName, int cursorOffset) {
        try {
            if (TryParseDocumentBestEffort(
                cursorOffset: cursorOffset,
                text: text
            ) is not { } document) {
                return null;
            }
            if (WorldDocumentEmitter.LowerWithDiagnostics(document).Value?["state"] is not JsonObject stateSection) {
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
            (PuckParser.ParseDocumentWithDiagnostics(source).Value is { } document) &&
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
        var line = ((int?)@params?["position"]?["line"] ?? 0);
        var col = ((int?)@params?["position"]?["character"] ?? 0);

        if (
            m_documents.TryGetValue(
            key: uri,
            value: out var text
        ) &&
            TryGetDotAccessRowName(
            targetCol: col,
            targetLine: line,
            text: text,
            rowName: out var dotRowName
        )
        ) {
            var cursorOffset = (text.Split('\n').Take(count: line).Sum(selector: part => (part.Length + 1)) + col);
            var keyItems = GetStateRowKeyCompletions(
                cursorOffset: cursorOffset,
                rowName: dotRowName,
                text: text
            );

            if (keyItems is { Count: > 0 }) {
                await SendResponseAsync(
                    id: id,
                    result: new JsonObject { ["isIncomplete"] = false, ["items"] = keyItems }
                ).ConfigureAwait(continueOnCapturedContext: false);
                return;
            }
        }
        var items = new JsonArray();

        // 1. Directives & Keywords
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
        AddCompletion(
            detail: "Keyword: Addon capability request",
            insertText: "request ${1|Mutate,Observe,Emit|} \"${2:subject}\"",
            items: items,
            kind: 14,
            label: "request"
        );
        AddCompletion(
            detail: "Keyword: Machine memory watch",
            insertText: "watchMemory screen: ${1:0}, address: ${2:0x02000000}, length: ${3:4}",
            items: items,
            kind: 14,
            label: "watchMemory"
        );

        // 2. Sections
        AddCompletion(
            detail: "Section: Host window & presentation",
            insertText: "host {\n    width: ${1:1280}\n    height: ${2:720}\n    fullscreen: ${3:false}\n    targetHertz: ${4:60}\n}",
            items: items,
            kind: 7,
            label: "host"
        );
        AddCompletion(
            detail: "Section: Camera layouts & seat rigs",
            insertText: "views {\n    $0\n}",
            items: items,
            kind: 7,
            label: "views"
        );
        AddCompletion(
            detail: "Section: Camera seat rig",
            insertText: "seatRig \"${1:main}\" {\n    version: \"puck.camera.program.v1\"\n    operations: [\n        orbit(pitch: 0deg, yaw: 0deg, distance: 2.5m)\n    ]\n}",
            items: items,
            kind: 7,
            label: "seatRig"
        );
        AddCompletion(
            detail: "Section: View layout",
            insertText: "layout \"${1:main}\" {\n    $0\n}",
            items: items,
            kind: 7,
            label: "layout"
        );
        AddCompletion(
            detail: "Section: SDF CSG Solids",
            insertText: "solids: [\n    $0\n]",
            items: items,
            kind: 7,
            label: "solids"
        );
        AddCompletion(
            detail: "Section: Surface materials",
            insertText: "materials: [\n    $0\n]",
            items: items,
            kind: 7,
            label: "materials"
        );
        AddCompletion(
            detail: "Section: Game state definition",
            insertText: "state {\n    $0\n}",
            items: items,
            kind: 7,
            label: "state"
        );
        AddCompletion(
            detail: "Section: Reactive state rules",
            insertText: "rules: [\n    $0\n]",
            items: items,
            kind: 7,
            label: "rules"
        );
        AddCompletion(
            detail: "Section: WASM Addons",
            insertText: "addons: [\n    $0\n]",
            items: items,
            kind: 7,
            label: "addons"
        );

        // 3. Solid Types
        AddCompletion(
            detail: "Solid: Prism CSG primitive",
            insertText: "solid Prism \"${1:name}\" {\n    size: [1, 1, 1]\n}",
            items: items,
            kind: 7,
            label: "solid Prism"
        );
        AddCompletion(
            detail: "Solid: Superellipsoid CSG primitive",
            insertText: "solid Superellipsoid \"${1:name}\" {\n    radius: 1\n    roundness: [0.2, 0.2]\n}",
            items: items,
            kind: 7,
            label: "solid Superellipsoid"
        );
        AddCompletion(
            detail: "Solid: Sphere CSG primitive",
            insertText: "solid Sphere \"${1:name}\" {\n    radius: 1\n}",
            items: items,
            kind: 7,
            label: "solid Sphere"
        );
        AddCompletion(
            detail: "Solid: Box CSG primitive",
            insertText: "solid Box \"${1:name}\" {\n    size: [1, 1, 1]\n}",
            items: items,
            kind: 7,
            label: "solid Box"
        );
        AddCompletion(
            detail: "Solid: Cylinder CSG primitive",
            insertText: "solid Cylinder \"${1:name}\" {\n    radius: 1\n    height: 2\n}",
            items: items,
            kind: 7,
            label: "solid Cylinder"
        );

        // 4. Built-in Functions
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

        // 5. Units
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

        // 6. Gate/effect/rule sugar keywords
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
            detail: "Keyword: Reactive rule block",
            insertText: "rule \"${1:name}\" {\n    when $0\n}",
            items: items,
            kind: 14,
            label: "rule"
        );
        AddCompletion(
            detail: "Keyword: Rule-scoped binding",
            insertText: "bind ${1:name}: ${2|Int,Fixed|} = ${3:expression}",
            items: items,
            kind: 14,
            label: "bind"
        );
        AddCompletion(
            detail: "Keyword: pushState effect",
            insertText: "push ${1:row} = ${2:value}",
            items: items,
            kind: 14,
            label: "push"
        );
        AddCompletion(
            detail: "Keyword: countdownState effect",
            insertText: "countdown ${1:row}",
            items: items,
            kind: 14,
            label: "countdown"
        );
        AddCompletion(
            detail: "Keyword: removeStateCell effect",
            insertText: "remove ${1:row}",
            items: items,
            kind: 14,
            label: "remove"
        );
        AddCompletion(
            detail: "Keyword: scheduleState effect",
            insertText: "schedule ${1:row} in ${2:1s}",
            items: items,
            kind: 14,
            label: "schedule"
        );
        AddCompletion(
            detail: "Keyword: transformState effect",
            insertText: "transform ${1:row} = ${2:boardCombine}(${3:args})",
            items: items,
            kind: 14,
            label: "transform"
        );
        AddCompletion(
            detail: "Keyword: Atomic effect batch",
            insertText: "transaction {\n    $0\n} onFailure {\n}",
            items: items,
            kind: 14,
            label: "transaction"
        );
        AddCompletion(
            detail: "Keyword: transaction failure branch",
            insertText: "onFailure {\n    $0\n}",
            items: items,
            kind: 14,
            label: "onFailure"
        );
        AddCompletion(
            detail: "Keyword: Conditional effect — lowers to the 'if' effect",
            insertText: "if ${1:condition} {\n    $0\n}",
            items: items,
            kind: 14,
            label: "if"
        );
        AddCompletion(
            detail: "Keyword: 'if' alternate branch",
            insertText: "else {\n    $0\n}",
            items: items,
            kind: 14,
            label: "else"
        );
        AddCompletion(
            detail: "Keyword: Reconsidered decision",
            insertText: "decision {\n    periodSeconds: ${1:1s}\n    option \"${2:name}\" {\n        score: $0\n    }\n}",
            items: items,
            kind: 14,
            label: "decision"
        );
        AddCompletion(
            detail: "Keyword: Decision candidate",
            insertText: "option \"${1:name}\" {\n    score: $0\n}",
            items: items,
            kind: 14,
            label: "option"
        );
        AddCompletion(
            detail: "Keyword: Decision early-reconsider gate",
            insertText: "interrupt ${1:condition}",
            items: items,
            kind: 14,
            label: "interrupt"
        );
        AddCompletion(
            detail: "Keyword: Decision fallback effects",
            insertText: "onNoChoice {\n    $0\n}",
            items: items,
            kind: 14,
            label: "onNoChoice"
        );
        AddCompletion(
            detail: "Keyword: Creation-document shape row",
            insertText: "shape ${1:Box} \"${2:name}\" {\n    $0\n}",
            items: items,
            kind: 14,
            label: "shape"
        );
        AddCompletion(
            detail: "Section: Placement rows",
            insertText: "placements {\n    $0\n}",
            items: items,
            kind: 14,
            label: "placements"
        );
        AddCompletion(
            detail: "Keyword: One placement row",
            insertText: "placement \"${1:id}\" {\n    prototype: $0\n}",
            items: items,
            kind: 14,
            label: "placement"
        );
        AddCompletion(
            detail: "Declaration: keyed state.world row",
            insertText: "table ${1:name} : ${2|Int,Fixed,Bool,Text|} {\n    ${3:key} = $0\n}",
            items: items,
            kind: 14,
            label: "table"
        );
        AddCompletion(
            detail: "Declaration: scalar state.world row",
            insertText: "slot ${1:name} : ${2|Int,Fixed,Bool,Text|} = $0",
            items: items,
            kind: 14,
            label: "slot"
        );
        AddCompletion(
            detail: "Escape hatch: the explicit state.world row form",
            insertText: "row {\n    $0\n}",
            items: items,
            kind: 14,
            label: "row"
        );
        AddCompletion(
            detail: "Declaration: ordered-membership state.world row",
            insertText: "pile ${1:name} of ${2:tokenRow} {\n    $0\n}",
            items: items,
            kind: 14,
            label: "pile"
        );
        AddCompletion(
            detail: "Declaration: physical-lattice occupancy state.world row",
            insertText: "grid ${1:name} : ${2|Int,Bool|} dimensions(width: ${3:8}, depth: ${4:8})",
            items: items,
            kind: 14,
            label: "grid"
        );
        AddCompletion(
            detail: "Modifier: a grid's cell counts",
            insertText: "dimensions(width: ${1:8}, depth: ${2:8})",
            items: items,
            kind: 3,
            label: "dimensions"
        );
        AddCompletion(
            detail: "Modifier: a grid's wrapped axes",
            insertText: "wrap(${1|None,X,Y,Both|})",
            items: items,
            kind: 3,
            label: "wrap"
        );
        AddCompletion(
            detail: "Modifier: a grid's cubic cell edge",
            insertText: "cellSize(${1:1})",
            items: items,
            kind: 3,
            label: "cellSize"
        );
        AddCompletion(
            detail: "Modifier: a grid's minimum corner",
            insertText: "origin(${1:0}, ${2:0}, ${3:0})",
            items: items,
            kind: 3,
            label: "origin"
        );
        AddCompletion(
            detail: "Modifier: a grid's vertical resolve band",
            insertText: "band(${1:0})",
            items: items,
            kind: 3,
            label: "band"
        );
        AddCompletion(
            detail: "Modifier: a grid's unwritten-cell value",
            insertText: "empty(${1:0})",
            items: items,
            kind: 3,
            label: "empty"
        );
        AddCompletion(
            detail: "Modifier: names a row whose values are this grid's cell ordinals",
            insertText: "positions(${1:tokenRow})",
            items: items,
            kind: 3,
            label: "positions"
        );
        AddCompletion(
            detail: "Modifier: derives this grid from a tokens/codes row pair",
            insertText: "inverse(tokens: ${1:tokens}, codes: ${2:codes})",
            items: items,
            kind: 3,
            label: "inverse"
        );
        AddCompletion(
            detail: "Modifier: range and overflow policy",
            insertText: "bounds(minimum: ${1:0}, maximum: ${2:100})",
            items: items,
            kind: 3,
            label: "bounds"
        );
        AddCompletion(
            detail: "Modifier: per-second accumulation",
            insertText: "advance(perSecond: ${1:1})",
            items: items,
            kind: 3,
            label: "advance"
        );
        AddCompletion(
            detail: "Modifier: table cell-count ceiling",
            insertText: "capacity(${1:1})",
            items: items,
            kind: 3,
            label: "capacity"
        );
        AddCompletion(
            detail: "Modifier: opt a cell out of its row's behavior",
            insertText: "behavior(none)",
            items: items,
            kind: 3,
            label: "behavior"
        );

        // 7. Effect/predicate/kind discriminators (the `name(k: v, ...)` call-form escape hatch)
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

        var parseResult = PuckParser.ParseDocumentWithDiagnostics(text);
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
        var formatted = PuckFormatter.Format(
            insertSpaces: insertSpaces,
            source: text,
            tabSize: ((tabSize > 0)
            ? tabSize
            : 2)
        );
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

        if (string.IsNullOrEmpty(value: word)) {
            await SendResponseAsync(
                id: id,
                result: null
            ).ConfigureAwait(continueOnCapturedContext: false);
            return;
        }

        var offset = (text.Split('\n').Take(count: line).Sum(selector: part => (part.Length + 1)) + col);
        var docCard = (PuckHoverInfo.Declaration(
            offset: offset,
            source: text,
            word: word
        ) ?? (PuckHoverInfo.Builtin(word: word) ?? (GetDocumentationForWord(word: word) ?? GetStateRowHoverCard(
            text: text,
            word: word
        ))));

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
        var diagnosticsBag = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            text,
            diagnostics: diagnosticsBag
        );

        if (
            (parseResult.Value is not null) &&
            !(m_diagnoseDocument?.Invoke(
            parseResult.Value,
            (TryGetLocalPath(
                path: out var localPath,
                uri: uri
            )
            ? localPath
            : null),
            diagnosticsBag
        ) ?? false)
        ) {
            PuckLinter.Lint(
                parseResult.Value,
                diagnosticsBag
            );

            // Reference resolution needs a real directory to resolve a declared basis/import against, which only a
            // `file://` URI carries — an unsaved buffer publishes syntax-level lint alone.
            if (TryGetLocalPath(
                path: out var sourcePath,
                uri: uri
            )) {
                var loweringDiags = new DiagnosticBag();
                var sourceMap = new SourceMap();
                var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
                    document: parseResult.Value,
                    basePath: Path.GetDirectoryName(path: sourcePath),
                    sourceMap: sourceMap,
                    diagnostics: loweringDiags
                );

                diagnosticsBag.AddRange(diagnostics: loweringDiags);

                if (
                    (loweringResult.Value is not null) &&
                    !diagnosticsBag.HasErrors
                ) {
                    PuckLinter.LintReferences(
                        loweringResult.Value,
                        sourceMap,
                        diagnosticsBag,
                        sourcePath: sourcePath
                    );
                }
            }
        }

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

    /// <summary>Creates a new instance of the Puck Language Server over the given input and output streams.</summary>
    /// <param name="input">The stream to read LSP JSON-RPC messages from (e.g. Console.OpenStandardInput()).</param>
    /// <param name="output">The stream to write LSP JSON-RPC messages to (e.g. Console.OpenStandardOutput()).</param>
    /// <param name="diagnoseDocument">An optional schema dispatcher; returns true when it supplies the document's diagnostics.</param>
    /// <param name="completeDocument">An optional schema completion provider; null retains World completions.</param>
    public PuckLanguageServer(Stream input, Stream output, Func<DocumentNode, string?, DiagnosticBag, bool>? diagnoseDocument = null,
        Func<DocumentNode, JsonArray?>? completeDocument = null) {
        m_input = input;
        m_output = output;
        m_diagnoseDocument = diagnoseDocument;
        m_completeDocument = completeDocument;
    }
}
