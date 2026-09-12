using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Ast;
using Puck.World.Transpiler.Diagnostics;
using Puck.World.Transpiler.Formatting;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;

namespace Puck.World.Transpiler.Lsp;

/// <summary>High-performance, Native AOT-compatible Language Server Protocol (LSP) implementation for the Puck authoring language.</summary>
public sealed class PuckLanguageServer {
    private readonly Stream m_input;
    private readonly Stream m_output;
    private readonly Dictionary<string, string> m_documents = new(StringComparer.OrdinalIgnoreCase);
    private bool m_running = true;

    private static void AddNode(JsonArray array, JsonNode? node) {
        array.Add(node);
    }

    private static bool TryGetLocalPath(string uri, out string path) {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile) {
            path = parsed.LocalPath;
            return true;
        }
        path = "";
        return false;
    }

    /// <summary>Creates a new instance of the Puck Language Server over the given input and output streams.</summary>
    /// <param name="input">The stream to read LSP JSON-RPC messages from (e.g. Console.OpenStandardInput()).</param>
    /// <param name="output">The stream to write LSP JSON-RPC messages to (e.g. Console.OpenStandardOutput()).</param>
    public PuckLanguageServer(Stream input, Stream output) {
        m_input = input;
        m_output = output;
    }

    /// <summary>Runs the Language Server loop until shutdown or EOF.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default) {
        while (m_running && !cancellationToken.IsCancellationRequested) {
            var message = await ReadMessageAsync(m_input, cancellationToken).ConfigureAwait(false);
            if (message is null) {
                break;
            }

            try {
                var jsonNode = JsonNode.Parse(message);
                if (jsonNode is JsonObject requestObj) {
                    await HandleMessageAsync(requestObj).ConfigureAwait(false);
                }
            } catch (Exception ex) {
                // Log and continue
                await LogMessageAsync($"LSP parse error: {ex.Message}").ConfigureAwait(false);
            }
        }
    }

    private async Task HandleMessageAsync(JsonObject msg) {
        var id = msg["id"];
        var method = msg["method"]?.ToString();
        var @params = msg["params"] as JsonObject;

        if (method is null) {
            return;
        }

        switch (method) {
            case "initialize":
                await HandleInitializeAsync(id).ConfigureAwait(false);
                break;

            case "initialized":
                // Server ready notification
                break;

            case "textDocument/didOpen":
                if (@params?["textDocument"] is JsonObject openDoc) {
                    var uri = openDoc["uri"]?.ToString() ?? "";
                    var text = openDoc["text"]?.ToString() ?? "";
                    m_documents[uri] = text;
                    await PublishDiagnosticsAsync(uri, text).ConfigureAwait(false);
                }
                break;

            case "textDocument/didChange":
                if (@params?["textDocument"] is JsonObject changeDoc && @params["contentChanges"] is JsonArray changes) {
                    var uri = changeDoc["uri"]?.ToString() ?? "";
                    if (changes.Count > 0 && changes[^1] is JsonObject lastChange) {
                        var text = lastChange["text"]?.ToString() ?? "";
                        m_documents[uri] = text;
                        await PublishDiagnosticsAsync(uri, text).ConfigureAwait(false);
                    }
                }
                break;

            case "textDocument/didClose":
                if (@params?["textDocument"] is JsonObject closeDoc) {
                    var uri = closeDoc["uri"]?.ToString() ?? "";
                    m_documents.Remove(uri);
                    await SendNotificationAsync("textDocument/publishDiagnostics", new JsonObject {
                        ["uri"] = uri,
                        ["diagnostics"] = new JsonArray()
                    }).ConfigureAwait(false);
                }
                break;

            case "textDocument/completion":
                await HandleCompletionAsync(id, @params).ConfigureAwait(false);
                break;

            case "textDocument/hover":
                await HandleHoverAsync(id, @params).ConfigureAwait(false);
                break;

            case "textDocument/documentSymbol":
                await HandleDocumentSymbolAsync(id, @params).ConfigureAwait(false);
                break;

            case "textDocument/formatting":
                await HandleFormattingAsync(id, @params).ConfigureAwait(false);
                break;

            case "shutdown":
                m_running = false;
                await SendResponseAsync(id, null).ConfigureAwait(false);
                break;

            case "exit":
                m_running = false;
                break;

            default:
                if (id is not null) {
                    // Method not found
                    await SendResponseAsync(id, null).ConfigureAwait(false);
                }
                break;
        }
    }

    private async Task HandleInitializeAsync(JsonNode? id) {
        var capabilities = new JsonObject {
            ["capabilities"] = new JsonObject {
                ["textDocumentSync"] = 1, // Full
                ["completionProvider"] = new JsonObject {
                    ["resolveProvider"] = false,
                    ["triggerCharacters"] = new JsonArray(
                        (JsonNode)JsonValue.Create(".")!,
                        (JsonNode)JsonValue.Create(":")!,
                        (JsonNode)JsonValue.Create("$")!,
                        (JsonNode)JsonValue.Create("@")!,
                        (JsonNode)JsonValue.Create(" ")!,
                        (JsonNode)JsonValue.Create("\"")!,
                        (JsonNode)JsonValue.Create("#")!
                    )
                },
                ["hoverProvider"] = true,
                ["documentSymbolProvider"] = true,
                ["documentFormattingProvider"] = true
            },
            ["serverInfo"] = new JsonObject {
                ["name"] = "Puck Language Server",
                ["version"] = "1.0.0"
            }
        };

        await SendResponseAsync(id, capabilities).ConfigureAwait(false);
    }

    private async Task PublishDiagnosticsAsync(string uri, string text) {
        var diagnosticsBag = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(text, diagnostics: diagnosticsBag);

        if (parseResult.Value is not null) {
            PuckLinter.Lint(parseResult.Value, diagnosticsBag);

            // Reference resolution needs a real directory to resolve a declared basis/import against, which only a
            // `file://` URI carries — an unsaved buffer publishes syntax-level lint alone.
            if (TryGetLocalPath(uri, out var sourcePath)) {
                var loweringDiags = new DiagnosticBag();
                var sourceMap = new SourceMap();
                var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
                    document: parseResult.Value,
                    basePath: Path.GetDirectoryName(sourcePath),
                    sourceMap: sourceMap,
                    diagnostics: loweringDiags
                );
                diagnosticsBag.AddRange(loweringDiags);

                if (loweringResult.Value is not null && !diagnosticsBag.HasErrors) {
                    PuckLinter.LintReferences(loweringResult.Value, sourceMap, diagnosticsBag, sourcePath: sourcePath);
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

            var startLine = Math.Max(0, diag.Span.Line - 1);
            var startCol = Math.Max(0, diag.Span.Column - 1);
            var endCol = startCol + Math.Max(1, diag.Span.Length);

            AddNode(lspDiags, new JsonObject {
                ["range"] = new JsonObject {
                    ["start"] = new JsonObject { ["line"] = startLine, ["character"] = startCol },
                    ["end"] = new JsonObject { ["line"] = startLine, ["character"] = endCol }
                },
                ["severity"] = severity,
                ["code"] = diag.Code,
                ["source"] = "puck",
                ["message"] = diag.Message
            });
        }

        await SendNotificationAsync("textDocument/publishDiagnostics", new JsonObject {
            ["uri"] = uri,
            ["diagnostics"] = lspDiags
        }).ConfigureAwait(false);
    }

    private async Task HandleCompletionAsync(JsonNode? id, JsonObject? @params) {
        var items = new JsonArray();

        // 1. Directives & Keywords
        AddCompletion(items, "schema", "schema: \"puck.world.def.v1\"", "Directive: Schema declaration", 14);
        AddCompletion(items, "basis", "basis: \"worlds/standard.basis.json\"", "Directive: Base world inheritance", 14);
        AddCompletion(items, "documentId", "documentId: \"my-world-id\"", "Directive: Document ID tag", 14);
        AddCompletion(items, "let", "let ${1:name} = ${2:value}", "Keyword: Declare constant", 14);
        AddCompletion(items, "template", "template ${1:name}(${2:params}) {\n    $0\n}", "Keyword: Parametric template", 14);
        AddCompletion(items, "import", "import \"${1:path}\" as ${2:alias}", "Keyword: Module import", 14);
        AddCompletion(items, "export", "export ${1|action,binding,read|} ${2:names}", "Keyword: Facet export", 14);
        AddCompletion(items, "request", "request ${1|Mutate,Observe,Emit|} \"${2:subject}\"", "Keyword: Addon capability request", 14);
        AddCompletion(items, "watchMemory", "watchMemory screen: ${1:0}, address: ${2:0x02000000}, length: ${3:4}", "Keyword: Machine memory watch", 14);

        // 2. Sections
        AddCompletion(items, "host", "host {\n    width: ${1:1280}\n    height: ${2:720}\n    fullscreen: ${3:false}\n    targetHertz: ${4:60}\n}", "Section: Host window & presentation", 7);
        AddCompletion(items, "views", "views {\n    $0\n}", "Section: Camera layouts & seat rigs", 7);
        AddCompletion(items, "seatRig", "seatRig \"${1:main}\" {\n    version: \"puck.camera.v1\"\n    operations: [\n        orbit(pitch: 0deg, yaw: 0deg, distance: 2.5m)\n    ]\n}", "Section: Camera seat rig", 7);
        AddCompletion(items, "layout", "layout \"${1:main}\" {\n    $0\n}", "Section: View layout", 7);
        AddCompletion(items, "solids", "solids: [\n    $0\n]", "Section: SDF CSG Solids", 7);
        AddCompletion(items, "materials", "materials: [\n    $0\n]", "Section: Surface materials", 7);
        AddCompletion(items, "state", "state {\n    $0\n}", "Section: Game state definition", 7);
        AddCompletion(items, "rules", "rules: [\n    $0\n]", "Section: Reactive state rules", 7);
        AddCompletion(items, "addons", "addons: [\n    $0\n]", "Section: WASM Addons", 7);

        // 3. Solid Types
        AddCompletion(items, "solid Prism", "solid Prism \"${1:name}\" {\n    size: [1, 1, 1]\n}", "Solid: Prism CSG primitive", 7);
        AddCompletion(items, "solid Superellipsoid", "solid Superellipsoid \"${1:name}\" {\n    radius: 1\n    roundness: [0.2, 0.2]\n}", "Solid: Superellipsoid CSG primitive", 7);
        AddCompletion(items, "solid Sphere", "solid Sphere \"${1:name}\" {\n    radius: 1\n}", "Solid: Sphere CSG primitive", 7);
        AddCompletion(items, "solid Box", "solid Box \"${1:name}\" {\n    size: [1, 1, 1]\n}", "Solid: Box CSG primitive", 7);
        AddCompletion(items, "solid Cylinder", "solid Cylinder \"${1:name}\" {\n    radius: 1\n    height: 2\n}", "Solid: Cylinder CSG primitive", 7);

        // 4. Built-in Functions
        AddCompletion(items, "orbit", "orbit(pitch: ${1:0deg}, yaw: ${2:0deg}, distance: ${3:2.5m})", "Camera orbit operation", 3);
        AddCompletion(items, "fov", "fov(degrees: ${1:60})", "Camera field-of-view operation", 3);
        AddCompletion(items, "boardShift", "boardShift(${1:mask}, ${2:lattice}, ${3:dir})", "Bitwise lattice shift function", 3);
        AddCompletion(items, "clamp", "clamp(${1:val}, ${2:min}, ${3:max})", "Math clamp function", 3);

        // 5. Units
        AddCompletion(items, "s", "s", "Unit: Seconds", 11);
        AddCompletion(items, "ms", "ms", "Unit: Milliseconds", 11);
        AddCompletion(items, "hz", "hz", "Unit: Hertz (frequency)", 11);
        AddCompletion(items, "rad", "rad", "Unit: Radians", 11);
        AddCompletion(items, "deg", "deg", "Unit: Degrees", 11);
        AddCompletion(items, "m", "m", "Unit: Meters", 11);
        AddCompletion(items, "cm", "cm", "Unit: Centimeters", 11);
        AddCompletion(items, "mm", "mm", "Unit: Millimeters", 11);

        // 6. Gate/effect/rule sugar keywords
        AddCompletion(items, "when", "when ${1:condition}", "Keyword: Rule/option gate", 14);
        AddCompletion(items, "and", "and", "Keyword: Gate conjunction", 14);
        AddCompletion(items, "or", "or", "Keyword: Gate disjunction", 14);
        AddCompletion(items, "not", "not ${1:condition}", "Keyword: Gate negation", 14);
        AddCompletion(items, "as", "as ${1|Int,Fixed|}", "Keyword: Comparison kind annotation — wrap in (...) beside and/or", 14);
        AddCompletion(items, "rule", "rule \"${1:name}\" {\n    when $0\n}", "Keyword: Reactive rule block", 14);
        AddCompletion(items, "bind", "bind ${1:name}: ${2|Int,Fixed|} = ${3:expression}", "Keyword: Rule-scoped binding", 14);
        AddCompletion(items, "push", "push ${1:row} = ${2:value}", "Keyword: pushState effect", 14);
        AddCompletion(items, "countdown", "countdown ${1:row}", "Keyword: countdownState effect", 14);
        AddCompletion(items, "remove", "remove ${1:row}", "Keyword: removeStateCell effect", 14);
        AddCompletion(items, "schedule", "schedule ${1:row} in ${2:1s}", "Keyword: scheduleState effect", 14);
        AddCompletion(items, "transform", "transform ${1:row} = ${2:boardCombine}(${3:args})", "Keyword: transformState effect", 14);
        AddCompletion(items, "transaction", "transaction {\n    $0\n} onFailure {\n}", "Keyword: Atomic effect batch", 14);
        AddCompletion(items, "onFailure", "onFailure {\n    $0\n}", "Keyword: transaction failure branch", 14);
        AddCompletion(items, "decision", "decision {\n    periodSeconds: ${1:1s}\n    option \"${2:name}\" {\n        score: $0\n    }\n}", "Keyword: Reconsidered decision", 14);
        AddCompletion(items, "option", "option \"${1:name}\" {\n    score: $0\n}", "Keyword: Decision candidate", 14);
        AddCompletion(items, "interrupt", "interrupt ${1:condition}", "Keyword: Decision early-reconsider gate", 14);
        AddCompletion(items, "onNoChoice", "onNoChoice {\n    $0\n}", "Keyword: Decision fallback effects", 14);
        AddCompletion(items, "shape", "shape ${1:Box} \"${2:name}\" {\n    $0\n}", "Keyword: Creation-document shape row", 14);
        AddCompletion(items, "placements", "placements {\n    $0\n}", "Section: Placement rows", 14);
        AddCompletion(items, "placement", "placement \"${1:id}\" {\n    prototype: $0\n}", "Keyword: One placement row", 14);

        // 7. Effect/predicate/kind discriminators (the `name(k: v, ...)` call-form escape hatch)
        AddCompletion(items, "compareState", "compareState(state: \"${1:row}\", comparison: ${2|Equal,NotEqual,Less,LessOrEqual,Greater,GreaterOrEqual|}, value: ${3:0})", "Predicate: compareState", 3);
        AddCompletion(items, "compareValue", "compareValue(left: \"${1:expr}\", comparison: ${2|Equal,NotEqual,Less,LessOrEqual,Greater,GreaterOrEqual|}, right: \"${3:expr}\")", "Predicate: compareValue", 3);
        AddCompletion(items, "setState", "setState(state: \"${1:row}\", value: ${2:0})", "Effect: setState", 3);
        AddCompletion(items, "addState", "addState(state: \"${1:row}\", value: ${2:0})", "Effect: addState", 3);
        AddCompletion(items, "pushState", "pushState(state: \"${1:row}\", value: ${2:0})", "Effect: pushState", 3);
        AddCompletion(items, "countdownState", "countdownState(state: \"${1:row}\")", "Effect: countdownState", 3);
        AddCompletion(items, "removeStateCell", "removeStateCell(state: \"${1:row}\")", "Effect: removeStateCell", 3);
        AddCompletion(items, "scheduleState", "scheduleState(state: \"${1:row}\", delaySeconds: ${2:1})", "Effect: scheduleState", 3);
        AddCompletion(items, "Int", "Int", "CellKind: exact integer domain", 13);
        AddCompletion(items, "Fixed", "Fixed", "CellKind: fixed-point domain", 13);

        await SendResponseAsync(id, new JsonObject {
            ["isIncomplete"] = false,
            ["items"] = items
        }).ConfigureAwait(false);
    }

    private static void AddCompletion(JsonArray items, string label, string insertText, string detail, int kind) {
        AddNode(items, new JsonObject {
            ["label"] = label,
            ["kind"] = kind,
            ["detail"] = detail,
            ["insertText"] = insertText,
            ["insertTextFormat"] = 2 // Snippet
        });
    }

    private async Task HandleHoverAsync(JsonNode? id, JsonObject? @params) {
        var uri = @params?["textDocument"]?["uri"]?.ToString() ?? "";
        var line = @params?["position"]?["line"]?.GetValue<int>() ?? 0;
        var col = @params?["position"]?["character"]?.GetValue<int>() ?? 0;

        if (!m_documents.TryGetValue(uri, out var text)) {
            await SendResponseAsync(id, null).ConfigureAwait(false);
            return;
        }

        var word = GetWordAtPosition(text, line, col);
        if (string.IsNullOrEmpty(word)) {
            await SendResponseAsync(id, null).ConfigureAwait(false);
            return;
        }

        var docCard = GetDocumentationForWord(word) ?? GetStateRowHoverCard(text, word);
        if (docCard is null) {
            await SendResponseAsync(id, null).ConfigureAwait(false);
            return;
        }

        await SendResponseAsync(id, new JsonObject {
            ["contents"] = new JsonObject {
                ["kind"] = "markdown",
                ["value"] = docCard
            }
        }).ConfigureAwait(false);
    }

    private static string? GetWordAtPosition(string text, int targetLine, int targetCol) {
        var lines = text.Split('\n');
        if (targetLine < 0 || targetLine >= lines.Length) {
            return null;
        }

        var lineText = lines[targetLine];
        if (targetCol < 0 || targetCol >= lineText.Length) {
            return null;
        }

        var start = targetCol;
        while (start > 0 && (char.IsLetterOrDigit(lineText[start - 1]) || lineText[start - 1] == '_' || lineText[start - 1] == '$')) {
            start--;
        }

        var end = targetCol;
        while (end < lineText.Length && (char.IsLetterOrDigit(lineText[end]) || lineText[end] == '_' || lineText[end] == '$')) {
            end++;
        }

        return start < end ? lineText.Substring(start, end - start) : null;
    }

    private static string? GetDocumentationForWord(string word) => word switch {
        "schema" => "**`schema` Directive**\n\nDeclares the document schema family tag (e.g. `puck.world.def.v1`, `puck.creation.v1`). Enables semantic validation and schema conformance checks.",
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
        "fov" => "**`fov(degrees:)`**\n\nSets the camera vertical field-of-view in degrees.",
        "solids" => "**`solids` Section**\n\nCollection of Signed Distance Field (SDF) Constructive Solid Geometry (CSG) primitives evaluated by the raymarching engine.",
        "materials" => "**`materials` Section**\n\nSurface material properties including albedo color, roughness, metallic, and reflectance.",
        "state" => "**`state` Section**\n\nWorld state definitions including discrete values, lattices, and cell arrays.",
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
                    if (row is not JsonObject rowObj || rowObj["name"]?.ToString() != word) {
                        continue;
                    }
                    var kind = rowObj["kind"]?.ToString() ?? "?";
                    var facets = new List<string>();
                    if (rowObj["capacity"] is JsonValue capacity) {
                        facets.Add($"capacity {capacity}");
                    }
                    if (rowObj["domain"] is JsonValue domain) {
                        facets.Add($"domain {domain}");
                    }
                    var suffix = (facets.Count > 0) ? $" ({string.Join(", ", facets)})" : "";
                    return $"**`{word}`** — state row\n\nKind: `{kind}`{suffix}";
                }
            }
        } catch {
            return null;
        }
        return null;
    }

    private async Task HandleDocumentSymbolAsync(JsonNode? id, JsonObject? @params) {
        var uri = @params?["textDocument"]?["uri"]?.ToString() ?? "";
        if (!m_documents.TryGetValue(uri, out var text)) {
            await SendResponseAsync(id, new JsonArray()).ConfigureAwait(false);
            return;
        }

        var parseResult = PuckParser.ParseDocumentWithDiagnostics(text);
        var docNode = parseResult.Value;
        if (docNode is null) {
            await SendResponseAsync(id, new JsonArray()).ConfigureAwait(false);
            return;
        }

        var symbols = new JsonArray();
        foreach (var stmt in docNode.Statements) {
            if (stmt is BlockNode block) {
                var name = block.Name is not null ? $"{block.Identifier} \"{block.Name}\"" : block.Identifier;
                var blockSym = CreateSymbol(name, 5, block.Line - 1, block.Column - 1, block.Length);
                var children = new JsonArray();
                foreach (var child in block.Statements) {
                    if (child is BlockNode childBlock) {
                        var cName = childBlock.Name is not null ? $"{childBlock.Identifier} \"{childBlock.Name}\"" : childBlock.Identifier;
                        AddNode(children, CreateSymbol(cName, 5, childBlock.Line - 1, childBlock.Column - 1, childBlock.Length));
                    } else if (child is PropertyNode childProp) {
                        AddNode(children, CreateSymbol(childProp.Name, 7, childProp.Line - 1, childProp.Column - 1, childProp.Length));
                    }
                }
                blockSym["children"] = children;
                AddNode(symbols, blockSym);
            } else if (stmt is PropertyNode prop) {
                AddNode(symbols, CreateSymbol(prop.Name, 7, prop.Line - 1, prop.Column - 1, prop.Length));
            } else if (stmt is LetNode letNode) {
                AddNode(symbols, CreateSymbol($"let {letNode.Name}", 13, letNode.Line - 1, letNode.Column - 1, letNode.Length));
            } else if (stmt is TemplateNode tmpl) {
                AddNode(symbols, CreateSymbol($"template {tmpl.Name}", 11, tmpl.Line - 1, tmpl.Column - 1, tmpl.Length));
            } else if (stmt is RuleBlockNode ruleBlock) {
                AddNode(symbols, CreateRuleSymbol(ruleBlock));
            }
        }

        await SendResponseAsync(id, symbols).ConfigureAwait(false);
    }

    private static JsonObject CreateRuleSymbol(RuleBlockNode rule) {
        var ruleSymbol = CreateSymbol($"rule \"{rule.Name}\"", 5, rule.Line - 1, rule.Column - 1, rule.Length);
        var children = new JsonArray();
        foreach (var stmt in rule.Statements) {
            switch (stmt) {
                case WhenStatementNode when1:
                    AddNode(children, CreateSymbol("when", 6, when1.Line - 1, when1.Column - 1, when1.Length));
                    break;
                case BindStatementNode bindStmt:
                    AddNode(children, CreateSymbol($"bind {bindStmt.Name}", 13, bindStmt.Line - 1, bindStmt.Column - 1, bindStmt.Length));
                    break;
                case DecisionBlockNode decisionStmt:
                    AddNode(children, CreateSymbol("decision", 5, decisionStmt.Line - 1, decisionStmt.Column - 1, decisionStmt.Length));
                    break;
                case PropertyNode propStmt:
                    AddNode(children, CreateSymbol(propStmt.Name, 7, propStmt.Line - 1, propStmt.Column - 1, propStmt.Length));
                    break;
            }
        }
        ruleSymbol["children"] = children;
        return ruleSymbol;
    }

    private static JsonObject CreateSymbol(string name, int kind, int line, int character, int length) {
        return new JsonObject {
            ["name"] = name,
            ["kind"] = kind,
            ["range"] = new JsonObject {
                ["start"] = new JsonObject { ["line"] = line, ["character"] = character },
                ["end"] = new JsonObject { ["line"] = line, ["character"] = character + Math.Max(1, length) }
            },
            ["selectionRange"] = new JsonObject {
                ["start"] = new JsonObject { ["line"] = line, ["character"] = character },
                ["end"] = new JsonObject { ["line"] = line, ["character"] = character + Math.Max(1, length) }
            }
        };
    }

    private async Task HandleFormattingAsync(JsonNode? id, JsonObject? @params) {
        var uri = @params?["textDocument"]?["uri"]?.ToString() ?? "";
        if (!m_documents.TryGetValue(uri, out var text)) {
            await SendResponseAsync(id, new JsonArray()).ConfigureAwait(false);
            return;
        }

        var formatted = PuckFormatter.Format(text);
        var lines = text.Split('\n');
        var lastLine = Math.Max(0, lines.Length - 1);
        var lastChar = lines.Length > 0 ? lines[^1].Length : 0;

        var edits = new JsonArray();
        AddNode(edits, new JsonObject {
            ["range"] = new JsonObject {
                ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                ["end"] = new JsonObject { ["line"] = lastLine, ["character"] = lastChar }
            },
            ["newText"] = formatted
        });

        await SendResponseAsync(id, edits).ConfigureAwait(false);
    }

    private async Task SendResponseAsync(JsonNode? id, JsonNode? result) {
        var response = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result
        };
        await WriteMessageAsync(m_output, response.ToJsonString()).ConfigureAwait(false);
    }

    private async Task SendNotificationAsync(string method, JsonObject @params) {
        var notification = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params
        };
        await WriteMessageAsync(m_output, notification.ToJsonString()).ConfigureAwait(false);
    }

    private async Task LogMessageAsync(string message) {
        await SendNotificationAsync("window/logMessage", new JsonObject {
            ["type"] = 4, // Info
            ["message"] = message
        }).ConfigureAwait(false);
    }

    private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken) {
        var contentLength = -1;
        var headerBuffer = new List<byte>();

        while (true) {
            var b = stream.ReadByte();
            if (b == -1) {
                return null;
            }

            headerBuffer.Add((byte)b);
            if (headerBuffer.Count >= 4 &&
                headerBuffer[^4] == '\r' && headerBuffer[^3] == '\n' &&
                headerBuffer[^2] == '\r' && headerBuffer[^1] == '\n') {
                var headerText = Encoding.ASCII.GetString(headerBuffer.ToArray());
                foreach (var line in headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries)) {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) {
                        var lengthStr = line.Substring("Content-Length:".Length).Trim();
                        int.TryParse(lengthStr, out contentLength);
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
            var read = await stream.ReadAsync(bodyBuffer.AsMemory(bytesRead, contentLength - bytesRead), cancellationToken).ConfigureAwait(false);
            if (read == 0) {
                return null;
            }
            bytesRead += read;
        }

        return Encoding.UTF8.GetString(bodyBuffer);
    }

    private static async Task WriteMessageAsync(Stream stream, string json) {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {bytes.Length}\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        await stream.WriteAsync(headerBytes).ConfigureAwait(false);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }
}
