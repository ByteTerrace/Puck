using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>LSP coverage for the `.puck` DSL sugar wave: completion for the new keywords, hover on a declared state
/// row's kind, and documentSymbol entries for `rule` blocks.</summary>
public class LspSugarTests {
    private const string SourceWithRuleAndStateRow = "puck: 1\nstate {\n    world: [\n        {\n            name: \"hp\"\n            kind: \"Int\"\n            capacity: 1\n        }\n    ]\n}\nrule \"heal\" {\n    when hp < 10\n    hp += 1\n}\n";

    private static Task<JsonNode> HoverMarkedAsync(string markedSource) {
        var offset = markedSource.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: '|'
        );
        var before = markedSource[..offset];
        var line = before.Count(predicate: character => (character == '\n'));
        var column = ((offset - before.LastIndexOf(value: '\n')) - 1);
        var source = markedSource.Remove(
            count: 1,
            startIndex: offset
        );

        return SendRequestsAsync(
            source,
            (2,
            $$$$"""{"jsonrpc":"2.0","id":2,"method":"textDocument/hover","params":{"textDocument":{"uri":"file:///sugar.puck"},"position":{"line":{{{{line}}}},"character":{{{{column}}}}}}}""")
        );
    }
    // Reads messages until one whose "id" equals `id`, skipping notifications (e.g. publishDiagnostics) along the way.
    private static JsonNode ReadResponseWithId(Stream stream, int id) {
        while (true) {
            var raw = ReadRpcMessage(stream: stream);

            Assert.NotNull(@object: raw);
            var node = JsonNode.Parse(raw)!;

            if (node["id"]?.GetValue<int>() == id) {
                return node;
            }
        }
    }
    private static string? ReadRpcMessage(Stream stream) {
        var headerBuffer = new List<byte>();
        var contentLength = -1;

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
                        var lenStr = line.Substring(startIndex: "Content-Length:".Length).Trim();

                        int.TryParse(
                            result: out contentLength,
                            s: lenStr
                        );
                    }
                }
                break;
            }
        }

        if (contentLength <= 0) {
            return null;
        }

        var body = new byte[contentLength];
        var read = 0;

        while (read < contentLength) {
            var r = stream.Read(
                buffer: body,
                count: (contentLength - read),
                offset: read
            );

            if (r == 0) {
                return null;
            }
            read += r;
        }

        return Encoding.UTF8.GetString(bytes: body);
    }
    private static async Task<JsonNode> SendRequestsAsync(string documentText, params (int Id, string Json)[] requests) {
        using var clientToServer = new MemoryStream();
        using var serverToClient = new MemoryStream();

        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}",
            stream: clientToServer
        );
        var openText = documentText.Replace(
            newValue: "\\\\",
            oldValue: "\\"
        ).Replace(
            newValue: "\\\"",
            oldValue: "\""
        ).Replace(
            newValue: "\\n",
            oldValue: "\n"
        );

        WriteRpcMessage(
            json: $"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"file:///sugar.puck\",\"languageId\":\"puck\",\"version\":1,\"text\":\"{openText}\"}}}}}}",
            stream: clientToServer
        );
        foreach (var (_, json) in requests) {
            WriteRpcMessage(
                json: json,
                stream: clientToServer
            );
        }
        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":9999,\"method\":\"shutdown\"}",
            stream: clientToServer
        );
        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}",
            stream: clientToServer
        );
        clientToServer.Position = 0;

        var server = new PuckLanguageServer(
            clientToServer,
            serverToClient
        );

        await server.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        serverToClient.Position = 0;
        JsonNode? last = null;

        foreach (var (id, _) in requests) {
            last = ReadResponseWithId(
                id: id,
                stream: serverToClient
            );
        }
        return last!;
    }
    private static void WriteRpcMessage(Stream stream, string json) {
        var bytes = Encoding.UTF8.GetBytes(s: json);
        var header = $"Content-Length: {bytes.Length}\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(s: header);

        stream.Write(buffer: headerBytes);
        stream.Write(buffer: bytes);
        stream.Flush();
    }

    [Fact]
    public async Task CompletionOffersTheNewGateAndEffectKeywords() {
        var response = await SendRequestsAsync(
            SourceWithRuleAndStateRow,
            (2, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/completion\",\"params\":{\"textDocument\":{\"uri\":\"file:///sugar.puck\"},\"position\":{\"line\":0,\"character\":0}}}")
        );

        var labels = response["result"]?["items"]?.AsArray()
            .Select(selector: item => item?["label"]?.ToString())
            .Where(predicate: label => (label is not null))
            .ToHashSet(comparer: StringComparer.Ordinal);

        Assert.NotNull(@object: labels);
        foreach (var keyword in new[] { "when", "and", "or", "not", "bind", "push", "countdown", "remove", "schedule", "transform", "transaction", "onFailure", "decision", "option", "interrupt", "onNoChoice", "rule", "shape", "placements", "placement" }) {
            Assert.Contains(
                expected: keyword,
                set: labels
            );
        }
    }
    [Fact]
    public async Task DocumentSymbolReportsARuleBlockWithItsGateAsAChild() {
        var response = await SendRequestsAsync(
            SourceWithRuleAndStateRow,
            (2, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/documentSymbol\",\"params\":{\"textDocument\":{\"uri\":\"file:///sugar.puck\"}}}")
        );

        var symbols = response["result"]?.AsArray();

        Assert.NotNull(@object: symbols);
        var ruleSymbol = symbols.FirstOrDefault(predicate: s => (s?["name"]?.ToString() == "rule \"heal\""));

        Assert.NotNull(@object: ruleSymbol);
        var children = ruleSymbol!["children"]?.AsArray();

        Assert.NotNull(@object: children);
        Assert.Contains(
            collection: children,
            filter: child => (child?["name"]?.ToString() == "when")
        );
    }
    [InlineData(2, true, "  ")]
    [InlineData(4, true, "    ")]
    [InlineData(4, false, "\t")]
    [Theory]
    public async Task FormattingHonorsClientIndentation(int tabSize, bool insertSpaces, string indent) {
        var request = System.Text.Json.JsonSerializer.Serialize(new {
            jsonrpc = "2.0",
            id = 2,
            method = "textDocument/formatting",
            @params = new { textDocument = new { uri = "file:///sugar.puck" }, options = new { tabSize, insertSpaces } }
        });
        var response = await SendRequestsAsync(
            "host { width: 1280, height: 720 }",
            (2, request)
        );

        Assert.Equal(
            $"host {{\n{indent}width: 1280,\n{indent}height: 720\n}}\n",
            response["result"]?[0]?["newText"]?.ToString()
        );
    }
    [InlineData("let seats = 4\ncount: foreign.sea|ts\n")]
    [InlineData("let seats = 4\n// sea|ts\n")]
    [InlineData("let seats = 4\n/* sea|ts */\n")]
    [InlineData("let seats = 4\nlabel: \"sea|ts\"\n")]
    [InlineData("let seats = 4\ncount: seats| + 1\n")]
    [InlineData("template tile(size = 2) { width: size }\nwidth: si|ze\n")]
    [InlineData("rule \"x\" {\nbind amount : Int = 2\nhp += amount\n}\nrule \"y\" { hp += amo|unt }\n")]
    [InlineData("let data = { exp|onent: 2.7 }\n")]
    [InlineData("schema: \"puck.cartridge.v1\"\nshape Box \"x\" { exp|onent: 2.7 }\n")]
    [InlineData("schema: \"puck.creation.v1\"\nnoise { rough|ness: 0.5 }\n")]
    [Theory]
    public async Task HoverDoesNotInventOutOfScopeOrNonCodeSymbols(string source) {
        var response = await HoverMarkedAsync(markedSource: source);

        Assert.Null(@object: response["result"]);
    }
    [InlineData("na|me: \"weathered-limestone\"", "name", "name")]
    [InlineData("palette [{ co|lor: \"#888778\" }]", "palette.color", "base color")]
    [InlineData("palette [{ rough|ness: 0.93 }]", "palette.roughness", "GGX roughness")]
    [InlineData("palette [{ spec|ular: 0.06 }]", "palette.specular", "specular strength")]
    [InlineData("noise { freq|uency: 1.8 }", "noise.frequency", "frequency")]
    [InlineData("noise { amp|litude: 0.04 }", "noise.amplitude", "amplitude")]
    [InlineData("noise { oct|aves: 2 }", "noise.octaves", "octave")]
    [InlineData("noise { ga|in: 0.4 }", "noise.gain", "gain")]
    [InlineData("noise { se|ed: 7341 }", "noise.seed", "seed")]
    [InlineData("shape Superellipsoid \"stone\" { pos|ition [0, 0.43, 0] }", "shape.position", "position")]
    [InlineData("shape Superellipsoid \"stone\" { sc|ale [0.95, 0.75, 0.7] }", "shape.scale", "per-axis scale")]
    [InlineData("shape Superellipsoid \"stone\" { exp|onent: 2.7 }", "shape.exponent", "exponent")]
    [InlineData("shape Superellipsoid \"stone\" { rot|ation [0, 0, 0, 1] }", "shape.rotation", "orientation")]
    [InlineData("shape Superellipsoid \"stone\" { mat|erial: 0 }", "shape.material", "palette slot")]
    [InlineData("shape Superellipsoid \"stone\" { bl|end: SmoothUnion }", "shape.blend", "blend op")]
    [InlineData("shape Superellipsoid \"stone\" { smo|oth: 0.16 }", "shape.smooth", "smooth-blend radius")]
    [InlineData("shape Super|ellipsoid \"stone\" { exponent: 2.7 }", "shape.type: Superellipsoid", "Superellipsoid")]
    [InlineData("shape Superellipsoid \"stone\" { blend: Smooth|Union }", "blend: SmoothUnion", "SmoothUnion")]
    [Theory]
    public async Task HoverExplainsCreationFieldsInsideAWorldPrototype(string body, string field, string description) {
        var source = (("schema: \"puck.world.definition.v1\"\nprototypes { prototype \"limestone\" { document {\nschema: \"puck.creation.v1\"\n" + body) + "\n} } }\n");
        var response = await HoverMarkedAsync(markedSource: source);
        var text = response["result"]?["contents"]?["value"]?.ToString();

        Assert.NotNull(@object: text);
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: field
        );
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: description
        );
    }
    [Fact]
    public async Task HoverOnADeclaredStateRowReportsItsKind() {
        // "hp" sits at line 12 ("    hp += 1"), 0-based.
        var response = await SendRequestsAsync(
            SourceWithRuleAndStateRow,
            (2, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"file:///sugar.puck\"},\"position\":{\"line\":12,\"character\":5}}}")
        );

        var hoverText = response["result"]?["contents"]?["value"]?.ToString();

        Assert.NotNull(@object: hoverText);
        Assert.Contains(
            actualString: hoverText,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "hp"
        );
        Assert.Contains(
            actualString: hoverText,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Int"
        );
    }
    [InlineData("// Number of seats.\nlet seats = 4\ncount: sea|ts\n", "let seats = 4", "Number of seats.")]
    [InlineData("count: sea|ts\nlet seats = 4\n", "let seats = 4", "compile-time constant")]
    [InlineData("template tile(size = 2) { width: size }\nt|ile()\n", "template tile(size = 2)", "template")]
    [InlineData("let size = 99\ntemplate tile(size = 2) { width: si|ze }\n", "size = 2", "template parameter")]
    [InlineData("template tile(size = 2) { width: size }\ntile(si|ze: 3)\n", "size = 2", "template parameter")]
    [InlineData("let item = 99\nvalues: map([1, 2], item => it|em + 1)\n", "item => item + 1", "lambda parameter")]
    [InlineData("let item = 99\nvalues: map([1, 2], item => item + 1)\ncount: it|em\n", "let item = 99", "compile-time constant")]
    [InlineData("for item in range(0, 2) { value: it|em }\n", "for item in range(0, 2)", "loop variable")]
    [InlineData("rule \"x\" {\nbind amount : Int = 2\nhp += amo|unt\n}\n", "bind amount : Int = 2", "rule binding (Int)")]
    [InlineData("values: ra|nge(3, 5)\n", "range(start, count)", "count consecutive integers")]
    [InlineData("value: cla|mp(4, 0, 3)\n", "clamp(arg1, arg2, arg3)", "Arguments: 3")]
    [InlineData("let seats = 4\nbroken: [\ncount: sea|ts\n", "let seats = 4", "compile-time constant")]
    [Theory]
    public async Task HoverResolvesDeclarationsAndFunctions(string markedSource, string signature, string description) {
        var response = await HoverMarkedAsync(markedSource: markedSource);
        var text = response["result"]?["contents"]?["value"]?.ToString();

        Assert.NotNull(@object: text);
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: signature
        );
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: description
        );
    }
    [InlineData("schema: \"puck.creation.v1\"\nshape Box \"x\" { mat|erial: 0 }", "palette slot")]
    [InlineData("host { wi|dth: 1280 }", "width in pixels")]
    [InlineData("value: orbit(ya|w: 0deg)", "heading in radians")]
    [InlineData("value: setState(st|ate: \"hp\", value: 3)", "state row name")]
    [Theory]
    public async Task HoverUsesOwningSchemaForFieldsAndCallArguments(string source, string expected) {
        var response = await HoverMarkedAsync(markedSource: source);
        var text = response["result"]?["contents"]?["value"]?.ToString();

        Assert.NotNull(@object: text);
        Assert.Contains(
            actualString: text,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: expected
        );
    }
    [Fact]
    public void SchemaExportsCreationHelp() {
        var schema = WorldSchema.Export(postRenderExtensions: []);
        var descriptions = (string.Join(
            separator: "\n",
            values: schema.Sections.Select(selector: section => section.Node.ToJsonString())
        ) + schema.Common.ToJsonString());

        Assert.Contains(
            actualString: descriptions,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "GGX roughness"
        );
        Assert.Contains(
            actualString: descriptions,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "x-enumDescriptions"
        );
        var prototypes = schema.Sections.Single(predicate: section => (section.Name == "prototypes")).Node;
        var name = prototypes["items"]?["properties"]?["document"]?["properties"]?["name"];

        Assert.NotNull(@object: name?["description"]);
        // Hover annotations must not activate WorldJsonContext converter constraints in the creation serializer.
        Assert.Null(@object: name?["type"]);
    }
}
