using System.Text;
using System.Text.Json.Nodes;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.Transpiler.Units;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>LSP tests for the embedded State SQL dialect: completions, dot-access column completions, and hover info.</summary>
public class StateSqlLspTests {
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

    private static async Task<JsonNode> CompletionAtAsync(string markedSource) {
        var offset = markedSource.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: '|'
        );
        var before = markedSource[..offset];
        var line = before.Count(predicate: character => (character == '\n'));
        var lastNewline = before.LastIndexOf(value: '\n');
        var column = (offset - lastNewline - 1);
        var source = markedSource.Remove(
            count: 1,
            startIndex: offset
        );

        return await SendRequestsAsync(
            source,
            (2,
            $$$$"""{"jsonrpc":"2.0","id":2,"method":"textDocument/completion","params":{"textDocument":{"uri":"file:///sql_test.puck"},"position":{"line":{{{{line}}}},"character":{{{{column}}}}}}}""")
        ).ConfigureAwait(continueOnCapturedContext: false);
    }

    private static async Task<JsonNode> HoverAtAsync(string markedSource) {
        var offset = markedSource.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: '|'
        );
        var before = markedSource[..offset];
        var line = before.Count(predicate: character => (character == '\n'));
        var lastNewline = before.LastIndexOf(value: '\n');
        var column = (offset - lastNewline - 1);
        var source = markedSource.Remove(
            count: 1,
            startIndex: offset
        );

        return await SendRequestsAsync(
            source,
            (2,
            $$$$"""{"jsonrpc":"2.0","id":2,"method":"textDocument/hover","params":{"textDocument":{"uri":"file:///sql_test.puck"},"position":{"line":{{{{line}}}},"character":{{{{column}}}}}}}""")
        ).ConfigureAwait(continueOnCapturedContext: false);
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
            json: $"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"file:///sql_test.puck\",\"languageId\":\"puck\",\"version\":1,\"text\":\"{openText}\"}}}}}}",
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
            serverToClient,
            vocabularyResolver: DefaultTestResolver
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

    private sealed class CartridgeTestVocabulary : IDocumentVocabulary {
        public static CartridgeTestVocabulary Instance { get; } = new();
        public UnitDimension ClassifyField(string fieldKey) => UnitDimension.None;
        public string? NameCallArgument(string callName, int positionalIndex) => null;
        public bool IsEmbeddedLanguage(string identifier) => false;
    }

    private static readonly DocumentVocabularyResolver DefaultTestResolver = new(
        vocabularies: new Dictionary<string, IDocumentVocabulary>(StringComparer.Ordinal) {
            ["puck.cartridge.v1"] = CartridgeTestVocabulary.Instance,
            [WorldDocumentVocabulary.Schema] = WorldDocumentVocabulary.Instance,
        },
        fallback: WorldDocumentVocabulary.Instance
    );

    private static HashSet<string> CompletionLabels(JsonNode response) => (response["result"]?["items"]?.AsArray()
        .Select(selector: item => item?["label"]?.ToString())
        .Where(predicate: static label => (label is not null))
        .Select(selector: static label => label!)
        .ToHashSet(comparer: StringComparer.Ordinal) ?? []);

    [Fact]
    public async Task CompletionInsideSqlBlockOffersSqlKeywords() {
        var source = "schema: \"puck.world.definition.v1\"\nsql {\n    |\n}\n";
        var response = await CompletionAtAsync(markedSource: source);
        var labels = CompletionLabels(response: response);

        Assert.Contains(
            expected: "CREATE TABLE",
            set: labels
        );
        Assert.Contains(
            expected: "DECLARE",
            set: labels
        );
        Assert.Contains(
            expected: "SELECT",
            set: labels
        );
        Assert.Contains(
            expected: "INSERT INTO",
            set: labels
        );
        Assert.Contains(
            expected: "UPDATE",
            set: labels
        );
        Assert.Contains(
            expected: "DELETE FROM",
            set: labels
        );
        Assert.Contains(
            expected: "BEGIN TRANSACTION",
            set: labels
        );
        Assert.Contains(
            expected: "ON OVERFLOW SATURATE",
            set: labels
        );
        Assert.Contains(
            expected: "INT",
            set: labels
        );
        Assert.Contains(
            expected: "KEY",
            set: labels
        );
    }

    [Fact]
    public async Task CompletionInsideSqlBlockOffersDeclaredTablesAndColumns() {
        var source = "schema: \"puck.world.definition.v1\"\nsql {\n    CREATE TABLE fighters (\n        hero KEY(16),\n        hp INT\n    );\n    DECLARE turnCount INT;\n    |\n}\n";
        var response = await CompletionAtAsync(markedSource: source);
        var labels = CompletionLabels(response: response);

        Assert.Contains(
            expected: "fighters",
            set: labels
        );
        Assert.Contains(
            expected: "hero",
            set: labels
        );
        Assert.Contains(
            expected: "hp",
            set: labels
        );
        Assert.Contains(
            expected: "turnCount",
            set: labels
        );
    }

    [Fact]
    public async Task DotCompletionOnSqlTableOffersColumns() {
        var source = "schema: \"puck.world.definition.v1\"\nsql {\n    CREATE TABLE fighters (\n        hero KEY(16),\n        hp INT\n    );\n}\nrule \"check\" {\n    when fighters.|\n    hp += 1\n}\n";
        var response = await CompletionAtAsync(markedSource: source);
        var labels = CompletionLabels(response: response);

        Assert.Contains(
            expected: "hero",
            set: labels
        );
        Assert.Contains(
            expected: "hp",
            set: labels
        );
    }

    [Fact]
    public async Task HoverOnSqlTableReportsSchema() {
        var source = "schema: \"puck.world.definition.v1\"\nsql {\n    CREATE TABLE figh|ters (\n        hero TEXT PRIMARY KEY,\n        hp INT CHECK (hp BETWEEN 0 AND 100)\n    ) CAPACITY 16;\n}\n";
        var response = await HoverAtAsync(markedSource: source);
        var markdown = response["result"]?["contents"]?["value"]?.ToString();

        Assert.NotNull(@object: markdown);
        Assert.Contains(
            actualString: markdown,
            expectedSubstring: "**`fighters`** — SQL table"
        );
        Assert.Contains(
            actualString: markdown,
            expectedSubstring: "Primary key: `hero` (capacity: 16)"
        );
        Assert.Contains(
            actualString: markdown,
            expectedSubstring: "fightersHp"
        );
    }

    [Fact]
    public async Task HoverOnSqlColumnReportsParentTableAndMappedRow() {
        var source = "schema: \"puck.world.definition.v1\"\nsql {\n    CREATE TABLE fighters (\n        hero TEXT PRIMARY KEY,\n        h|p INT CHECK (hp BETWEEN 0 AND 100) ON OVERFLOW SATURATE\n    ) CAPACITY 16;\n}\n";
        var response = await HoverAtAsync(markedSource: source);
        var markdown = response["result"]?["contents"]?["value"]?.ToString();

        Assert.NotNull(@object: markdown);
        Assert.Contains(
            actualString: markdown,
            expectedSubstring: "**`hp`** — SQL column (`fighters.hp`)"
        );
        Assert.Contains(
            actualString: markdown,
            expectedSubstring: "State row: `fightersHp`"
        );
        Assert.Contains(
            actualString: markdown,
            expectedSubstring: "Check: `BETWEEN 0 AND 100`"
        );
        Assert.Contains(
            actualString: markdown,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "Overflow: `Saturate`"
        );
    }

    [Fact]
    public async Task HoverOnSqlSlotReportsTypeAndStateRow() {
        var source = "schema: \"puck.world.definition.v1\"\nsql {\n    DECLARE turn|Count INT DEFAULT 1;\n}\n";
        var response = await HoverAtAsync(markedSource: source);
        var markdown = response["result"]?["contents"]?["value"]?.ToString();

        Assert.NotNull(@object: markdown);
        Assert.Contains(
            actualString: markdown,
            expectedSubstring: "**`turnCount`** — SQL scalar slot"
        );
        Assert.Contains(
            actualString: markdown,
            expectedSubstring: "State row: `turnCount`"
        );
    }

    [Fact]
    public async Task HoverOnSqlKeywordReportsDocumentation() {
        var source = "schema: \"puck.world.definition.v1\"\nsql {\n    CREATE TABLE fighters (\n        hero KEY(16),\n        hp INT ON OVERFLOW SATU|RATE\n    );\n}\n";
        var response = await HoverAtAsync(markedSource: source);
        var markdown = response["result"]?["contents"]?["value"]?.ToString();

        Assert.NotNull(@object: markdown);
        Assert.Contains(
            actualString: markdown,
            expectedSubstring: "**`ON OVERFLOW SATURATE`** — SQL dialect keyword"
        );
    }

    [Fact]
    public async Task HoverOutsideSqlBlockDoesNotReportSqlHover() {
        var source = "schema: \"puck.world.definition.v1\"\n\nsql {\n    CREATE TABLE fighters (\n        hero TEXT PRIMARY KEY,\n        hp INT\n    );\n}\n\nstate {\n    world {\n        slot h|p : Int = 10\n    }\n}\n";
        var response = await HoverAtAsync(markedSource: source);
        var markdown = response["result"]?["contents"]?["value"]?.ToString();

        if (markdown is not null) {
            Assert.DoesNotContain("SQL column", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("SQL table", markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CompletionOutsideSqlBlockDoesNotOfferSqlKeywords() {
        var source = "schema: \"puck.world.definition.v1\"\n\nsql {\n    DECLARE turnCount INT DEFAULT 1;\n}\n\nstate {\n    world {\n        slot mysqlRow = |\n    }\n}\n";
        var response = await CompletionAtAsync(markedSource: source);
        var items = response["result"]?["items"] as JsonArray;
        if (items is not null) {
            var labels = items.Select(i => i?["label"]?.ToString()).Where(l => l is not null).ToList();
            Assert.DoesNotContain("CREATE TABLE", labels);
            Assert.DoesNotContain("DECLARE", labels);
            Assert.DoesNotContain("INSERT INTO", labels);
        }
    }

    [Fact]
    public async Task CartridgeDocumentWithSqlBlock_GetsNoSqlHoverOrCompletion() {
        var source = "schema: \"puck.cartridge.v1\"\n\nsql {\n    CREATE TABLE fi|ghters (\n        hero TEXT PRIMARY KEY\n    );\n}\n";
        var hoverResponse = await HoverAtAsync(markedSource: source);
        var markdown = hoverResponse["result"]?["contents"]?["value"]?.ToString();
        if (markdown is not null) {
            Assert.DoesNotContain("SQL table", markdown, StringComparison.Ordinal);
        }

        var completionResponse = await CompletionAtAsync(markedSource: source);
        var items = completionResponse["result"]?["items"] as JsonArray;
        if (items is not null) {
            var labels = items.Select(i => i?["label"]?.ToString()).Where(l => l is not null).ToList();
            Assert.DoesNotContain("CREATE TABLE", labels);
            Assert.DoesNotContain("DECLARE", labels);
        }
    }

    [Fact]
    public void WorldDocumentWhoseCommentContainsCartridgeSchema_ParsedAsWorld() {
        var source = "// schema: \"puck.cartridge.v1\"\nschema: \"puck.world.definition.v1\"\n\nsql {\n    DECLARE counter INT = 0;\n}\n";
        Assert.True(PuckParser.TryReadDocumentSchema(source, out var schema));
        Assert.Equal("puck.world.definition.v1", schema);
    }
}
