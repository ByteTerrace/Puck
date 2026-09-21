using System.Text;
using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lsp;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>CONTRACT UNDER TEST: the language server publishes what <see cref="WorldSourceDiagnostics"/> reports and
/// nothing else, so a world the engine refuses is refused in the editor by the same code, text and line, whether
/// the buffer is a saved file or has never been saved.</summary>
public class EditorDiagnosticsLawTests {
    // Parses, lowers and lints clean; only the engine's own validation refuses it.
    private const string Refused = """
        schema: "puck.world.definition.v1"
        documentId: "refused"

        state {
          world {
            slot armour = 0
          }
        }

        rule "reads-a-cell-the-row-lacks" {
          when armour[plate] == 1
          mode: Edge
          armour = 2
        }
        """;

    private static async Task<List<(string Code, string Message, int Line)>> PublishedAsync(string uri, string source) {
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
                        ["uri"] = uri,
                        ["languageId"] = "puck",
                        ["version"] = 1,
                        ["text"] = source,
                    },
                },
            }.ToJsonString(),
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
        await new PuckLanguageServer(
            clientToServer,
            serverToClient
        ).RunAsync(cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(continueOnCapturedContext: true);
        serverToClient.Position = 0;

        var published = new List<(string Code, string Message, int Line)>();

        while (ReadRpcMessage(stream: serverToClient) is { } message) {
            if (JsonNode.Parse(json: message)?["params"]?["diagnostics"] is not JsonArray entries) {
                continue;
            }
            foreach (var entry in entries) {
                published.Add(item: (
                    entry!["code"]!.ToString(),
                    entry["message"]!.ToString(),
                    entry["range"]!["start"]!["line"]!.GetValue<int>()
                ));
            }
        }

        return published;
    }
    private static string? ReadRpcMessage(Stream stream) {
        var header = new List<byte>();

        while (true) {
            var value = stream.ReadByte();

            if (value < 0) {
                return null;
            }

            header.Add(item: ((byte)value));
            if (
                (header.Count >= 4) &&
                (header[^4] == '\r') &&
                (header[^3] == '\n') &&
                (header[^2] == '\r') &&
                (header[^1] == '\n')
            ) {
                break;
            }
        }

        var length = int.Parse(s: Encoding.ASCII.GetString(bytes: [.. header])
            .Split(separator: ':')[1]
            .Trim());
        var body = new byte[length];

        stream.ReadExactly(buffer: body);

        return Encoding.UTF8.GetString(bytes: body);
    }
    private static List<(string Code, string Message, int Line)> Reported(string source, string? sourcePath) =>
        [.. WorldSourceDiagnostics.Diagnose(
            source: source,
            sourcePath: sourcePath
        ).Select(selector: static diagnostic => (
            diagnostic.Code,
            diagnostic.Message,
            Math.Max(
                val1: 0,
                val2: (diagnostic.Span.Line - 1)
            )
        ))];
    private static void WriteRpcMessage(Stream stream, string json) {
        var body = Encoding.UTF8.GetBytes(s: json);

        stream.Write(buffer: Encoding.ASCII.GetBytes(s: $"Content-Length: {body.Length}\r\n\r\n"));
        stream.Write(buffer: body);
    }

    [Fact]
    public async Task AWorldTheEngineRefusesIsRefusedInTheEditorByTheSameCodeTextAndLine() {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-editor-law-{Guid.NewGuid():N}"
        );

        _ = Directory.CreateDirectory(path: directory);
        try {
            var path = Path.Combine(
                path1: directory,
                path2: "refused.puck"
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            );

            await File.WriteAllTextAsync(
                cancellationToken: TestContext.Current.CancellationToken,
                contents: Refused,
                path: path
            );

            var saved = Reported(
                source: Refused,
                sourcePath: path
            );
            var refusal = Assert.Single(collection: saved);

            Assert.Equal(
                actual: refusal.Code,
                expected: PuckDiagnosticCodes.SemanticValidation
            );
            Assert.Equal(
                actual: refusal.Line,
                expected: 10
            );
            Assert.Equal(
                saved,
                await PublishedAsync(
                    source: Refused,
                    uri: new Uri(uriString: path).AbsoluteUri
                )
            );
            Assert.Equal(
                saved,
                await PublishedAsync(
                    source: Refused,
                    uri: "untitled:refused"
                )
            );
        } finally {
            Directory.Delete(
                path: directory,
                recursive: true
            );
        }
    }
}
