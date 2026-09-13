using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class LspTests {
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileUrisResolveCanonicalBasis(bool escapeDriveColon) {
        var sourcePath = Path.Combine(ShippedWorlds.FindDirectory(), "moth-courtyard.puck");
        var uri = new Uri(sourcePath).AbsoluteUri;
        if (escapeDriveColon && OperatingSystem.IsWindows()) {
            var colon = uri.IndexOf(':', "file:///".Length);
            Assert.True(colon >= 0);
            uri = uri[..colon] + "%3A" + uri[(colon + 1)..];
        }
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        WriteRpcMessage(input, System.Text.Json.JsonSerializer.Serialize(new {
            jsonrpc = "2.0", method = "textDocument/didOpen",
            @params = new { textDocument = new { uri, languageId = "puck", version = 1,
                text = "schema: \"puck.world.def.v1\"\nbasis: \"avatars/moth.puck\"\n" } }
        }));
        WriteRpcMessage(input, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"shutdown\"}");
        input.Position = 0;
        await new PuckLanguageServer(input, output).RunAsync(TestContext.Current.CancellationToken);
        output.Position = 0;
        var notification = JsonNode.Parse(ReadRpcMessage(output)!);
        Assert.Equal("textDocument/publishDiagnostics", notification?["method"]?.ToString());
        var diagnostics = notification?["params"]?["diagnostics"]?.AsArray();
        Assert.NotNull(diagnostics);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic?["code"]?.ToString() == "PUCK035");
    }
    [Fact]
    public async Task TestLspInitializeAndShutdown() {
        using var clientToServer = new MemoryStream();
        using var serverToClient = new MemoryStream();

        WriteRpcMessage(clientToServer, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");
        WriteRpcMessage(clientToServer, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"shutdown\"}");
        WriteRpcMessage(clientToServer, "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}");
        clientToServer.Position = 0;

        var server = new PuckLanguageServer(clientToServer, serverToClient);
        await server.RunAsync(TestContext.Current.CancellationToken);

        serverToClient.Position = 0;
        var response1 = ReadRpcMessage(serverToClient);
        Assert.NotNull(response1);

        var json1 = JsonNode.Parse(response1);
        Assert.NotNull(json1);
        var capabilities = json1!["result"]?["capabilities"];
        Assert.NotNull(capabilities);
        Assert.True(capabilities!["hoverProvider"]?.GetValue<bool>());
        Assert.True(capabilities!["documentFormattingProvider"]?.GetValue<bool>());

        var response2 = ReadRpcMessage(serverToClient);
        Assert.NotNull(response2);
        var json2 = JsonNode.Parse(response2);
        Assert.Equal(2, json2!["id"]?.GetValue<int>());
    }

    [Fact]
    public async Task TestLspHoverAndFormatting() {
        using var clientToServer = new MemoryStream();
        using var serverToClient = new MemoryStream();

        // 1. initialize
        WriteRpcMessage(clientToServer, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");
        // 2. didOpen
        WriteRpcMessage(clientToServer, "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///test.puck\",\"languageId\":\"puck\",\"version\":1,\"text\":\"puck: 1\\nhost: {\\nauthority: \\\"test\\\"\\n}\\n\"}}}");
        // 3. hover on "host"
        WriteRpcMessage(clientToServer, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"file:///test.puck\"},\"position\":{\"line\":1,\"character\":1}}}");
        // 4. formatting
        WriteRpcMessage(clientToServer, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"textDocument/formatting\",\"params\":{\"textDocument\":{\"uri\":\"file:///test.puck\"}}}");
        // 5. shutdown & exit
        WriteRpcMessage(clientToServer, "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"shutdown\"}");
        WriteRpcMessage(clientToServer, "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}");
        clientToServer.Position = 0;

        var server = new PuckLanguageServer(clientToServer, serverToClient);
        await server.RunAsync(TestContext.Current.CancellationToken);

        serverToClient.Position = 0;
        // 1. initialize response
        var initResp = ReadRpcMessage(serverToClient);
        Assert.NotNull(initResp);

        // 2. didOpen may trigger publishDiagnostics notification
        var msg2 = ReadRpcMessage(serverToClient);
        Assert.NotNull(msg2);
        var json2 = JsonNode.Parse(msg2);

        // Check if msg2 is diagnostics notification or if hover response comes next
        JsonNode? hoverResp;
        if (json2?["method"]?.ToString() == "textDocument/publishDiagnostics") {
            var msg3 = ReadRpcMessage(serverToClient);
            hoverResp = JsonNode.Parse(msg3!);
        } else {
            hoverResp = json2;
        }

        Assert.NotNull(hoverResp);
        Assert.Equal(2, hoverResp!["id"]?.GetValue<int>());
        var hoverContents = hoverResp!["result"]?["contents"]?["value"]?.ToString();
        Assert.NotNull(hoverContents);
        Assert.Contains("host", hoverContents, StringComparison.OrdinalIgnoreCase);

        // 3. formatting response
        var fmtMsg = ReadRpcMessage(serverToClient);
        Assert.NotNull(fmtMsg);
        var fmtResp = JsonNode.Parse(fmtMsg);
        Assert.Equal(3, fmtResp!["id"]?.GetValue<int>());
        var edits = fmtResp!["result"]?.AsArray();
        Assert.NotNull(edits);
        Assert.True(edits.Count > 0);
        var newText = edits[0]?["newText"]?.ToString();
        Assert.NotNull(newText);
        Assert.Contains("  authority: \"test\"", newText);
    }
}
