using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>LSP tests for state embeddings: completions, document symbols for spaces, keyword hover, and embedded text lock status.</summary>
public class EmbeddingDeclarationLspTests {
    private const string SampleVectorBase64 = "fwAAAAAAAAA"; // 8-byte unit vector

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
                        int.TryParse(result: out contentLength, s: lenStr);
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

    private static async Task<JsonNode> SendRequestsAsync(string documentText, string? fileUri, params (int Id, string Json)[] requests) {
        using var clientToServer = new MemoryStream();
        using var serverToClient = new MemoryStream();

        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}",
            stream: clientToServer
        );

        var uri = (fileUri ?? "file:///test_embeddings.puck");
        var didOpenJson = System.Text.Json.JsonSerializer.Serialize(new {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new {
                textDocument = new {
                    uri,
                    languageId = "puck",
                    version = 1,
                    text = documentText
                }
            }
        });

        WriteRpcMessage(
            json: didOpenJson,
            stream: clientToServer
        );

        foreach (var (_, json) in requests) {
            WriteRpcMessage(json: json, stream: clientToServer);
        }

        WriteRpcMessage(json: "{\"jsonrpc\":\"2.0\",\"id\":9999,\"method\":\"shutdown\"}", stream: clientToServer);
        WriteRpcMessage(json: "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}", stream: clientToServer);

        clientToServer.Position = 0;
        var server = new PuckLanguageServer(
            clientToServer,
            serverToClient
        );

        await server.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        serverToClient.Position = 0;
        JsonNode? last = null;
        foreach (var (id, _) in requests) {
            last = ReadResponseWithId(id: id, stream: serverToClient);
        }
        return last!;
    }

    private static async Task<JsonNode> CompletionAtAsync(string markedSource) {
        var offset = markedSource.IndexOf('|');
        var before = markedSource[..offset];
        var line = before.Count(c => c == '\n');
        var lastNewline = before.LastIndexOf('\n');
        var column = (offset - lastNewline - 1);
        var source = markedSource.Remove(startIndex: offset, count: 1);

        return await SendRequestsAsync(
            source,
            null,
            (2, $$$$"""{"jsonrpc":"2.0","id":2,"method":"textDocument/completion","params":{"textDocument":{"uri":"file:///test_embeddings.puck"},"position":{"line":{{{{line}}}},"character":{{{{column}}}}}}}""")
        );
    }

    private static async Task<JsonNode> HoverAtAsync(string markedSource, string? fileUri = null) {
        var offset = markedSource.IndexOf('|');
        var before = markedSource[..offset];
        var line = before.Count(c => c == '\n');
        var lastNewline = before.LastIndexOf('\n');
        var column = (offset - lastNewline - 1);
        var source = markedSource.Remove(startIndex: offset, count: 1);

        var uri = (fileUri ?? "file:///test_embeddings.puck");
        return await SendRequestsAsync(
            source,
            uri,
            (2, $$$$"""{"jsonrpc":"2.0","id":2,"method":"textDocument/hover","params":{"textDocument":{"uri":"{{{{uri}}}}"},"position":{"line":{{{{line}}}},"character":{{{{column}}}}}}}""")
        );
    }

    [Fact]
    public async Task CompletionsIncludeAllEmbeddingKeywordsAndTransforms() {
        var response = await CompletionAtAsync("""
            schema: "puck.world.definition.v1"

            |
            """);

        var items = Assert.IsType<JsonArray>(@object: response["result"]?["items"]);
        var labels = items.Select(i => i?["label"]?.ToString()).Where(l => l is not null).ToHashSet();

        string[] expectedKeywords = [
            "spaces", "space", "Vector", "evicts", "embeds", "embed",
            "vector", "dot", "similarity", "identical", "mix", "mean",
            "nearest", "remember"
        ];

        foreach (var keyword in expectedKeywords) {
            Assert.Contains(keyword, labels);
        }
    }

    [Fact]
    public async Task DocumentSymbolsIncludeSpacesBlockAndDefinedSpaces() {
        var source = """
            schema: "puck.world.definition.v1"

            state {
                spaces {
                    space lore {
                        model: "text-embedding-3-small"
                        revision: "1"
                        dimensions: 256
                    }
                }
            }
            """;

        var response = await SendRequestsAsync(
            source,
            null,
            (2, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/documentSymbol\",\"params\":{\"textDocument\":{\"uri\":\"file:///test_embeddings.puck\"}}}")
        );

        var symbols = Assert.IsType<JsonArray>(@object: response["result"]);
        Assert.NotEmpty(symbols);

        var stateSymbol = symbols.FirstOrDefault(s => s?["name"]?.ToString() == "state");
        Assert.NotNull(stateSymbol);

        var stateChildren = Assert.IsType<JsonArray>(@object: stateSymbol["children"]);
        var spacesSymbol = stateChildren.FirstOrDefault(s => s?["name"]?.ToString() == "spaces");
        Assert.NotNull(spacesSymbol);

        var spacesChildren = Assert.IsType<JsonArray>(@object: spacesSymbol["children"]);
        Assert.Single(spacesChildren);
        Assert.Equal("space lore", spacesChildren[0]?["name"]?.ToString());
    }

    [Fact]
    public async Task HoverOnKeywordReturnsDocumentationCard() {
        var response = await HoverAtAsync("""
            schema: "puck.world.definition.v1"

            state {
                world {
                    slot q : |Vector
                }
            }
            """);

        var card = response["result"]?["contents"]?["value"]?.ToString();
        Assert.NotNull(card);
        Assert.Contains("Vector", card);
        Assert.Contains("embedding vector", card);
    }

    [Fact]
    public async Task HoverOnEmbeddedTextWithLockShowsLockStatusAndNearest() {
        var tempPuckFile = Path.Combine(Path.GetTempPath(), "lsp_embed_test_" + Guid.NewGuid().ToString("N") + ".puck");
        var tempLockFile = EmbeddingLock.DeriveLockPath(sourcePath: tempPuckFile);

        try {
            var lockFile = new EmbeddingLock();
            var space = new EmbeddingLockSpace(dimensions: 8, model: "text-embedding-3-small", revision: "1");
            var h1 = EmbeddingLock.ComputeTextHash("hello world");
            space.Entries[h1] = new EmbeddingLockEntry(Text: "hello world", Vector: SampleVectorBase64);
            var h2 = EmbeddingLock.ComputeTextHash("peaceful morning");
            space.Entries[h2] = new EmbeddingLockEntry(Text: "peaceful morning", Vector: SampleVectorBase64);
            lockFile.Spaces["lore"] = space;
            lockFile.Write(lockPath: tempLockFile);

            var sourceWithCursor = """
                schema: "puck.world.definition.v1"

                state {
                    spaces {
                        space lore {
                            model: "text-embedding-3-small"
                            revision: "1"
                            dimensions: 8
                        }
                    }
                    world {
                        slot current : Vector space("lore")
                    }
                }
                rule "r" {
                    current = embed("hello| world")
                }
                """;

            var fileUri = new Uri(tempPuckFile).AbsoluteUri;
            var response = await HoverAtAsync(markedSource: sourceWithCursor, fileUri: fileUri);

            var card = response["result"]?["contents"]?["value"]?.ToString();
            Assert.NotNull(card);
            Assert.Contains("Embedded Text", card);
            Assert.Contains("Lock status:** Locked", card);
            Assert.Contains("Nearest locked texts:", card);
            Assert.Contains("peaceful morning", card);
        } finally {
            if (File.Exists(tempPuckFile)) {
                File.Delete(tempPuckFile);
            }
            if (File.Exists(tempLockFile)) {
                File.Delete(tempLockFile);
            }
        }
    }

    [Fact]
    public async Task HoverOnUnlockedEmbeddedTextShowsNotLocked() {
        var sourceWithCursor = """
            schema: "puck.world.definition.v1"

            state {
                spaces {
                    space lore {
                        model: "text-embedding-3-small"
                        revision: "1"
                        dimensions: 8
                    }
                }
                world {
                    slot current : Vector space("lore")
                }
            }
            rule "r" {
                current = embed("unlocked| text")
            }
            """;

        var response = await HoverAtAsync(markedSource: sourceWithCursor);
        var card = response["result"]?["contents"]?["value"]?.ToString();
        Assert.NotNull(card);
        Assert.Contains("Embedded Text", card);
        Assert.Contains("Not locked", card);
    }
}
