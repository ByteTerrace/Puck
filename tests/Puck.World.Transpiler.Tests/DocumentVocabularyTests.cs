using System.Text;
using System.Text.Json.Nodes;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.Transpiler.Units;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class DocumentVocabularyTests {
    private sealed class CustomVocabulary : IDocumentVocabulary {
        public const string Schema = "puck.custom.v1";
        public static CustomVocabulary Instance { get; } = new();
        public bool IsEmbeddedLanguage(string identifier) => false;
        public UnitDimension ClassifyField(string fieldKey) => UnitDimension.None;
        public string? NameCallArgument(string callName, int positionalIndex) => null;
    }

    [Fact]
    public void TestSubschemaNotMatched() {
        const string Source = "subschema: \"puck.world.definition.v1\"\n";
        var matched = PuckParser.TryReadDocumentSchema(source: Source, schema: out var schema);
        Assert.False(condition: matched);
        Assert.Null(@object: schema);
    }

    [Fact]
    public void TestRawStringContainingSchemaNotMatched() {
        const string Source = """"
            let text = """
            schema: "puck.world.definition.v1"
            """
            """";
        var matched = PuckParser.TryReadDocumentSchema(source: Source, schema: out var schema);
        Assert.False(condition: matched);
        Assert.Null(@object: schema);
    }

    [Fact]
    public void TestNestedBlockContainingSchemaNotMatched() {
        const string Source = """
            world {
                schema: "puck.world.definition.v1"
            }
            """;
        var matched = PuckParser.TryReadDocumentSchema(source: Source, schema: out var schema);
        Assert.False(condition: matched);
        Assert.Null(@object: schema);
    }

    [Fact]
    public void TestTopLevelSchemaMatched() {
        const string Source = """
            // Leading comment
            schema: "puck.custom.v1"

            custom {
            }
            """;
        var matched = PuckParser.TryReadDocumentSchema(source: Source, schema: out var schema);
        Assert.True(condition: matched);
        Assert.Equal(expected: "puck.custom.v1", actual: schema);
    }

    [Fact]
    public void TestDocumentVocabularyResolver() {
        var resolver = new DocumentVocabularyResolver(
            vocabularies: new Dictionary<string, IDocumentVocabulary> {
                [CustomVocabulary.Schema] = CustomVocabulary.Instance,
                [WorldDocumentVocabulary.Schema] = WorldDocumentVocabulary.Instance,
            },
            fallback: WorldDocumentVocabulary.Instance
        );

        var customVocab = resolver.Resolve(source: "schema: \"puck.custom.v1\"\n");
        Assert.Same(expected: CustomVocabulary.Instance, actual: customVocab);

        var worldVocab = resolver.Resolve(source: "schema: \"puck.world.definition.v1\"\n");
        Assert.Same(expected: WorldDocumentVocabulary.Instance, actual: worldVocab);

        var fallbackVocab = resolver.Resolve(source: "let x = 1\n");
        Assert.Same(expected: WorldDocumentVocabulary.Instance, actual: fallbackVocab);

        var subschemaVocab = resolver.Resolve(source: "subschema: \"puck.custom.v1\"\n");
        Assert.Same(expected: WorldDocumentVocabulary.Instance, actual: subschemaVocab);
    }

    [Fact]
    public async Task TestCustomDocumentInLspGetsNoSqlHoverOrCompletion() {
        var resolver = new DocumentVocabularyResolver(
            vocabularies: new Dictionary<string, IDocumentVocabulary> {
                [CustomVocabulary.Schema] = CustomVocabulary.Instance,
                [WorldDocumentVocabulary.Schema] = WorldDocumentVocabulary.Instance,
            },
            fallback: WorldDocumentVocabulary.Instance
        );

        const string MarkedSource = """
            schema: "puck.custom.v1"

            sql {
                SE|LECT 1;
            }
            """;

        var offset = MarkedSource.IndexOf(value: '|', comparisonType: StringComparison.Ordinal);
        var before = MarkedSource[..offset];
        var line = before.Count(predicate: c => c == '\n');
        var lastNewline = before.LastIndexOf(value: '\n');
        var column = offset - lastNewline - 1;
        var source = MarkedSource.Remove(startIndex: offset, count: 1);

        using var clientToServer = new MemoryStream();
        using var serverToClient = new MemoryStream();

        WriteRpc(clientToServer, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");

        var escapedSource = source.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        WriteRpc(clientToServer, $"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"file:///test.puck\",\"languageId\":\"puck\",\"version\":1,\"text\":\"{escapedSource}\"}}}}}}");

        WriteRpc(clientToServer, $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/completion\",\"params\":{{\"textDocument\":{{\"uri\":\"file:///test.puck\"}},\"position\":{{\"line\":{line},\"character\":{column}}}}}}}");
        WriteRpc(clientToServer, $"{{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"textDocument/hover\",\"params\":{{\"textDocument\":{{\"uri\":\"file:///test.puck\"}},\"position\":{{\"line\":{line},\"character\":{column}}}}}}}");
        WriteRpc(clientToServer, "{\"jsonrpc\":\"2.0\",\"id\":9999,\"method\":\"shutdown\"}");
        WriteRpc(clientToServer, "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}");

        clientToServer.Position = 0;

        var server = new PuckLanguageServer(
            input: clientToServer,
            output: serverToClient,
            vocabularyResolver: resolver
        );

        await server.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        serverToClient.Position = 0;

        var completionResponse = ReadResponse(serverToClient, id: 2);
        var hoverResponse = ReadResponse(serverToClient, id: 3);

        // Completion result should not contain SQL SELECT
        var completionResult = completionResponse["result"];
        if (completionResult is JsonArray completions) {
            Assert.DoesNotContain(completions, item => item?["label"]?.GetValue<string>() == "SELECT");
        }

        // Hover result should be null
        Assert.Null(hoverResponse["result"]);
    }

    private static void WriteRpc(Stream stream, string json) {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
        stream.Write(header);
        stream.Write(bytes);
    }

    private static JsonNode ReadResponse(Stream stream, int id) {
        while (true) {
            var raw = ReadRpcMessage(stream);
            Assert.NotNull(raw);
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
            headerBuffer.Add((byte)b);
            if (headerBuffer.Count >= 4 &&
                headerBuffer[^4] == '\r' &&
                headerBuffer[^3] == '\n' &&
                headerBuffer[^2] == '\r' &&
                headerBuffer[^1] == '\n') {
                var headerText = Encoding.ASCII.GetString(headerBuffer.ToArray());
                foreach (var line in headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries)) {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) {
                        int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
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
            var r = stream.Read(body, read, contentLength - read);
            if (r == 0) {
                return null;
            }
            read += r;
        }

        return Encoding.UTF8.GetString(body);
    }
}
