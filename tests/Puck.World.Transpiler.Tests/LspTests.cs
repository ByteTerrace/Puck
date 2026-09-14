using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class LspTests {
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
    private static void WriteRpcMessage(Stream stream, string json) {
        var bytes = Encoding.UTF8.GetBytes(s: json);
        var header = $"Content-Length: {bytes.Length}\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(s: header);

        stream.Write(buffer: headerBytes);
        stream.Write(buffer: bytes);
        stream.Flush();
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task FileUrisResolveCanonicalBasis(bool escapeDriveColon) {
        var sourcePath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: "moth-courtyard.puck"
        );
        var uri = new Uri(uriString: sourcePath).AbsoluteUri;

        if (
            escapeDriveColon &&
            OperatingSystem.IsWindows()
        ) {
            var colon = uri.IndexOf(
                ':',
                "file:///".Length
            );

            Assert.True(condition: (colon >= 0));
            uri = ((uri[..colon] + "%3A") + uri[(colon + 1)..]);
        }
        using var input = new MemoryStream();
        using var output = new MemoryStream();

        WriteRpcMessage(
            input,
            System.Text.Json.JsonSerializer.Serialize(new {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new {
                textDocument = new {
                    uri,
                    languageId = "puck",
                    version = 1,
                    text = "schema: \"puck.world.definition.v1\"\nbasis: \"avatars/moth.puck\"\n"
                }
            }
        })
        );
        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"shutdown\"}",
            stream: input
        );
        input.Position = 0;
        await new PuckLanguageServer(
            input,
            output
        ).RunAsync(cancellationToken: TestContext.Current.CancellationToken);
        output.Position = 0;
        var notification = JsonNode.Parse(ReadRpcMessage(stream: output)!);

        Assert.Equal(
            "textDocument/publishDiagnostics",
            notification?["method"]?.ToString()
        );
        var diagnostics = notification?["params"]?["diagnostics"]?.AsArray();

        Assert.NotNull(@object: diagnostics);
        Assert.DoesNotContain(
            collection: diagnostics,
            filter: diagnostic => (diagnostic?["code"]?.ToString() == "PUCK035")
        );
    }
    [Fact]
    public async Task TestLspHoverAndFormatting() {
        using var clientToServer = new MemoryStream();
        using var serverToClient = new MemoryStream();

        // 1. initialize
        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}",
            stream: clientToServer
        );
        // 2. didOpen
        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///test.puck\",\"languageId\":\"puck\",\"version\":1,\"text\":\"puck: 1\\nhost: {\\nauthority: \\\"test\\\"\\n}\\n\"}}}",
            stream: clientToServer
        );
        // 3. hover on "host"
        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"file:///test.puck\"},\"position\":{\"line\":1,\"character\":1}}}",
            stream: clientToServer
        );
        // 4. formatting
        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"textDocument/formatting\",\"params\":{\"textDocument\":{\"uri\":\"file:///test.puck\"}}}",
            stream: clientToServer
        );
        // 5. shutdown & exit
        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"shutdown\"}",
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
        // 1. initialize response
        var initResp = ReadRpcMessage(stream: serverToClient);

        Assert.NotNull(@object: initResp);

        // 2. didOpen may trigger publishDiagnostics notification
        var msg2 = ReadRpcMessage(stream: serverToClient);

        Assert.NotNull(@object: msg2);
        var json2 = JsonNode.Parse(msg2);

        // Check if msg2 is diagnostics notification or if hover response comes next
        JsonNode? hoverResp;

        if (json2?["method"]?.ToString() == "textDocument/publishDiagnostics") {
            var msg3 = ReadRpcMessage(stream: serverToClient);

            hoverResp = JsonNode.Parse(msg3!);
        } else {
            hoverResp = json2;
        }

        Assert.NotNull(@object: hoverResp);
        Assert.Equal(
            2,
            hoverResp!["id"]?.GetValue<int>()
        );
        var hoverContents = hoverResp!["result"]?["contents"]?["value"]?.ToString();

        Assert.NotNull(@object: hoverContents);
        Assert.Contains(
            actualString: hoverContents,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "host"
        );

        // 3. formatting response
        var fmtMsg = ReadRpcMessage(stream: serverToClient);

        Assert.NotNull(@object: fmtMsg);
        var fmtResp = JsonNode.Parse(fmtMsg);

        Assert.Equal(
            3,
            fmtResp!["id"]?.GetValue<int>()
        );
        var edits = fmtResp!["result"]?.AsArray();

        Assert.NotNull(@object: edits);
        Assert.True(condition: (edits.Count > 0));
        var newText = edits[0]?["newText"]?.ToString();

        Assert.NotNull(@object: newText);
        Assert.Contains(
            actualString: newText,
            expectedSubstring: "  authority: \"test\""
        );
    }
    [Fact]
    public async Task TestLspInitializeAndShutdown() {
        using var clientToServer = new MemoryStream();
        using var serverToClient = new MemoryStream();

        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}",
            stream: clientToServer
        );
        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"shutdown\"}",
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
        var response1 = ReadRpcMessage(stream: serverToClient);

        Assert.NotNull(@object: response1);

        var json1 = JsonNode.Parse(response1);

        Assert.NotNull(@object: json1);
        var capabilities = json1!["result"]?["capabilities"];

        Assert.NotNull(@object: capabilities);
        Assert.True(condition: capabilities!["hoverProvider"]?.GetValue<bool>());
        Assert.True(condition: capabilities!["documentFormattingProvider"]?.GetValue<bool>());

        var response2 = ReadRpcMessage(stream: serverToClient);

        Assert.NotNull(@object: response2);
        var json2 = JsonNode.Parse(response2);

        Assert.Equal(
            2,
            json2!["id"]?.GetValue<int>()
        );
    }
}
