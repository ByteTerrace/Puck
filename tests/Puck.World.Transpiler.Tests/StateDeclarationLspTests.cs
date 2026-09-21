using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>LSP completion for a dot-access read: completing a declared table's own cell keys after <c>row.</c>,
/// including recovery from an in-progress, not-yet-closed statement.</summary>
public class StateDeclarationLspTests {
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
            $$$$"""{"jsonrpc":"2.0","id":2,"method":"textDocument/completion","params":{"textDocument":{"uri":"file:///sugar.puck"},"position":{"line":{{{{line}}}},"character":{{{{column}}}}}}}""")
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
    private static HashSet<string> CompletionLabels(JsonNode response) => (response["result"]?["items"]?.AsArray()
        .Select(selector: item => item?["label"]?.ToString())
        .Where(predicate: static label => (label is not null))
        .Select(selector: static label => label!)
        .ToHashSet(comparer: StringComparer.Ordinal) ?? []);

    [Fact]
    public async Task CompletionAfterADotOffersTheDeclaredTablesOwnCellKeys() {
        var source = "schema: \"puck.world.definition.v1\"\nstate {\n    world {\n        table vitals {\n            health = 100\n            mana = 50\n        }\n    }\n}\nrule \"heal\" {\n    when vitals.|\n    hp += 1\n}\n";
        var response = await CompletionAtAsync(markedSource: source);
        var labels = CompletionLabels(response: response);

        Assert.Contains(
            expected: "health",
            set: labels
        );
        Assert.Contains(
            expected: "mana",
            set: labels
        );
        Assert.DoesNotContain(
            expected: "when",
            set: labels
        );
    }
    [Fact]
    public async Task CompletionAfterADotWithAPartialKeyStillOffersTheDeclaredCellKeys() {
        var source = "schema: \"puck.world.definition.v1\"\nstate {\n    world {\n        table vitals {\n            health = 100\n            mana = 50\n        }\n    }\n}\nrule \"heal\" {\n    when vitals.he|\n    hp += 1\n}\n";
        var response = await CompletionAtAsync(markedSource: source);
        var labels = CompletionLabels(response: response);

        Assert.Contains(
            expected: "health",
            set: labels
        );
    }
    [Fact]
    public async Task CompletionAfterADotOnIncompleteUnclosedInputStillRecoversTheDeclaredCellKeys() {
        // The `rule "heal" {` block is never closed anywhere in the buffer — this is what a document looks like
        // mid-keystroke, before the author has typed a closing brace.
        var source = "schema: \"puck.world.definition.v1\"\nstate {\n    world {\n        table vitals {\n            health = 100\n            mana = 50\n        }\n    }\n}\nrule \"heal\" {\n    when vitals.|";
        var response = await CompletionAtAsync(markedSource: source);
        var labels = CompletionLabels(response: response);

        Assert.Contains(
            expected: "health",
            set: labels
        );
        Assert.Contains(
            expected: "mana",
            set: labels
        );
    }
}
