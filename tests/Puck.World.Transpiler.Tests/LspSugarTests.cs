using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>LSP coverage for the `.puck` DSL sugar wave: completion for the new keywords, hover on a declared state
/// row's kind, and documentSymbol entries for `rule` blocks.</summary>
public class LspSugarTests {
    private const string SourceWithRuleAndStateRow = "puck: 1\n"
        + "state {\n"
        + "    world: [\n"
        + "        {\n"
        + "            name: \"hp\"\n"
        + "            kind: \"Int\"\n"
        + "            capacity: 1\n"
        + "        }\n"
        + "    ]\n"
        + "}\n"
        + "rule \"heal\" {\n"
        + "    when hp < 10\n"
        + "    hp += 1\n"
        + "}\n";

    private static void WriteRpcMessage(Stream stream, string json) {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {bytes.Length}\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes);
        stream.Write(bytes);
        stream.Flush();
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
                headerBuffer[^4] == '\r' && headerBuffer[^3] == '\n' &&
                headerBuffer[^2] == '\r' && headerBuffer[^1] == '\n') {
                var headerText = Encoding.ASCII.GetString(headerBuffer.ToArray());
                foreach (var line in headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries)) {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) {
                        var lenStr = line.Substring("Content-Length:".Length).Trim();
                        int.TryParse(lenStr, out contentLength);
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

    // Reads messages until one whose "id" equals `id`, skipping notifications (e.g. publishDiagnostics) along the way.
    private static JsonNode ReadResponseWithId(Stream stream, int id) {
        while (true) {
            var raw = ReadRpcMessage(stream);
            Assert.NotNull(raw);
            var node = JsonNode.Parse(raw)!;
            if (node["id"]?.GetValue<int>() == id) {
                return node;
            }
        }
    }

    private static async Task<JsonNode> SendRequestsAsync(string documentText, params (int Id, string Json)[] requests) {
        using var clientToServer = new MemoryStream();
        using var serverToClient = new MemoryStream();

        WriteRpcMessage(clientToServer, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");
        var openText = documentText.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        WriteRpcMessage(clientToServer, $"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"file:///sugar.puck\",\"languageId\":\"puck\",\"version\":1,\"text\":\"{openText}\"}}}}}}");
        foreach (var (_, json) in requests) {
            WriteRpcMessage(clientToServer, json);
        }
        WriteRpcMessage(clientToServer, "{\"jsonrpc\":\"2.0\",\"id\":9999,\"method\":\"shutdown\"}");
        WriteRpcMessage(clientToServer, "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}");
        clientToServer.Position = 0;

        var server = new PuckLanguageServer(clientToServer, serverToClient);
        await server.RunAsync(TestContext.Current.CancellationToken);

        serverToClient.Position = 0;
        JsonNode? last = null;
        foreach (var (id, _) in requests) {
            last = ReadResponseWithId(serverToClient, id);
        }
        return last!;
    }

    [Fact]
    public async Task CompletionOffersTheNewGateAndEffectKeywords() {
        var response = await SendRequestsAsync(
            SourceWithRuleAndStateRow,
            (2, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/completion\",\"params\":{\"textDocument\":{\"uri\":\"file:///sugar.puck\"},\"position\":{\"line\":0,\"character\":0}}}")
        );

        var labels = response["result"]?["items"]?.AsArray()
            .Select(item => item?["label"]?.ToString())
            .Where(label => label is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotNull(labels);
        foreach (var keyword in new[] { "when", "and", "or", "not", "bind", "push", "countdown", "remove", "schedule", "transform", "transaction", "onFailure", "decision", "option", "interrupt", "onNoChoice", "rule", "shape", "placements", "placement" }) {
            Assert.Contains(keyword, labels);
        }
    }

    [Fact]
    public async Task HoverOnADeclaredStateRowReportsItsKind() {
        // "hp" sits at line 12 ("    hp += 1"), 0-based.
        var response = await SendRequestsAsync(
            SourceWithRuleAndStateRow,
            (2, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"file:///sugar.puck\"},\"position\":{\"line\":12,\"character\":5}}}")
        );

        var hoverText = response["result"]?["contents"]?["value"]?.ToString();
        Assert.NotNull(hoverText);
        Assert.Contains("hp", hoverText, StringComparison.Ordinal);
        Assert.Contains("Int", hoverText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentSymbolReportsARuleBlockWithItsGateAsAChild() {
        var response = await SendRequestsAsync(
            SourceWithRuleAndStateRow,
            (2, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/documentSymbol\",\"params\":{\"textDocument\":{\"uri\":\"file:///sugar.puck\"}}}")
        );

        var symbols = response["result"]?.AsArray();
        Assert.NotNull(symbols);
        var ruleSymbol = symbols.FirstOrDefault(s => s?["name"]?.ToString() == "rule \"heal\"");
        Assert.NotNull(ruleSymbol);
        var children = ruleSymbol!["children"]?.AsArray();
        Assert.NotNull(children);
        Assert.Contains(children, child => child?["name"]?.ToString() == "when");
    }
}
