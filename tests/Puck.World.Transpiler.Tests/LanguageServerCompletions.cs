using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>One completion the language server offered.</summary>
/// <param name="Detail">The item's detail line, which is what says where the item came from.</param>
/// <param name="InsertText">The snippet the editor would insert.</param>
/// <param name="Label">The word the list shows.</param>
internal sealed record OfferedCompletion(string Label, string InsertText, string Detail);

/// <summary>Drives <see cref="PuckLanguageServer"/> over its own stdio protocol and returns the completions it
/// offers at one cursor position.</summary>
/// <remarks>The real entry point rather than <c>WorldConstructLanguageServices</c> directly, so what a law reads is
/// what an editor would receive, including the position-to-enclosing-construct resolution the handler does.</remarks>
internal static class LanguageServerCompletions {
    private const string Uri = "file:///probe.puck";

    private static string? ReadRpcMessage(Stream stream) {
        var header = new List<byte>();
        var length = -1;

        while (true) {
            var next = stream.ReadByte();

            if (next == -1) {
                return null;
            }
            header.Add(item: ((byte)next));
            if (
                (header.Count >= 4) &&
                (header[^4] == '\r') &&
                (header[^3] == '\n') &&
                (header[^2] == '\r') &&
                (header[^1] == '\n')
            ) {
                foreach (var line in Encoding.ASCII.GetString(bytes: [.. header]).Split(
                    options: StringSplitOptions.RemoveEmptyEntries,
                    separator: ["\r\n"]
                )) {
                    if (line.StartsWith(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: "Content-Length:"
                    )) {
                        _ = int.TryParse(
                            result: out length,
                            s: line["Content-Length:".Length..].Trim()
                        );
                    }
                }
                break;
            }
        }
        if (length <= 0) {
            return null;
        }

        var body = new byte[length];
        var read = 0;

        while (read < length) {
            var count = stream.Read(
                buffer: body,
                count: (length - read),
                offset: read
            );

            if (count == 0) {
                return null;
            }
            read += count;
        }

        return Encoding.UTF8.GetString(bytes: body);
    }
    private static void WriteRpcMessage(Stream stream, string json) {
        var bytes = Encoding.UTF8.GetBytes(s: json);

        stream.Write(buffer: Encoding.ASCII.GetBytes(s: $"Content-Length: {bytes.Length}\r\n\r\n"));
        stream.Write(buffer: bytes);
        stream.Flush();
    }

    /// <summary>Returns every completion the language server offers with the cursor at one position.</summary>
    /// <param name="character">The cursor's 0-based character within that line.</param>
    /// <param name="line">The cursor's 0-based line.</param>
    /// <param name="source">The document as authored.</param>
    /// <returns>The offered items, in the order the server sent them.</returns>
    public static IReadOnlyList<OfferedCompletion> At(string source, int line, int character) {
        using var clientToServer = new MemoryStream();
        using var serverToClient = new MemoryStream();

        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}",
            stream: clientToServer
        );
        WriteRpcMessage(
            json: new JsonObject {
                ["jsonrpc"] = "2.0",
                ["method"] = "textDocument/didOpen",
                ["params"] = new JsonObject {
                    ["textDocument"] = new JsonObject {
                        ["uri"] = Uri,
                        ["languageId"] = "puck",
                        ["version"] = 1,
                        ["text"] = source,
                    },
                },
            }.ToJsonString(),
            stream: clientToServer
        );
        WriteRpcMessage(
            json: new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "textDocument/completion",
                ["params"] = new JsonObject {
                    ["textDocument"] = new JsonObject { ["uri"] = Uri },
                    ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
                },
            }.ToJsonString(),
            stream: clientToServer
        );
        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"shutdown\"}",
            stream: clientToServer
        );
        WriteRpcMessage(
            json: "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}",
            stream: clientToServer
        );
        clientToServer.Position = 0;
        new PuckLanguageServer(
            clientToServer,
            serverToClient
        ).RunAsync(cancellationToken: TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        serverToClient.Position = 0;

        while (ReadRpcMessage(stream: serverToClient) is { } message) {
            var parsed = JsonNode.Parse(json: message);

            if (
                (parsed?["id"]?.GetValue<int>() != 2) ||
                (parsed["result"]?["items"] is not JsonArray offered)
            ) {
                continue;
            }

            return [.. offered.Select(selector: static item => new OfferedCompletion(
                Detail: (item?["detail"]?.ToString() ?? ""),
                InsertText: (item?["insertText"]?.ToString() ?? ""),
                Label: (item?["label"]?.ToString() ?? "")
            ))];
        }

        return [];
    }
}
