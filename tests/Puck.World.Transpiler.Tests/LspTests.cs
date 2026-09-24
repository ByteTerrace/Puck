using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class LspTests {
    // A message's length counts UTF-8 bytes, so a body with multi-byte characters is where a character count would
    // go wrong.
    [Fact]
    public async Task AFramedMessageReadsBackAsWrittenAndTheStreamEndsCleanly() {
        const string Json = "{\"jsonrpc\":\"2.0\",\"method\":\"note\",\"params\":{\"text\":\"größe — 大きさ\"}}";
        using var stream = new MemoryStream();

        await LspFraming.WriteAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            json: Json,
            stream: stream
        );
        await LspFraming.WriteAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            json: Json,
            stream: stream
        );
        stream.Position = 0;

        for (var index = 0; (index < 2); index++) {
            Assert.Equal(
                actual: await LspFraming.ReadAsync(
                    cancellationToken: TestContext.Current.CancellationToken,
                    stream: stream
                ),
                expected: Json
            );
        }
        Assert.Null(@object: await LspFraming.ReadAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stream: stream
        ));
    }
    [InlineData("Content-Length: 5\r\n")]
    [InlineData("Content-Length: 5\r\n\r\n{}")]
    [InlineData("Content-Type: application/json\r\n\r\n{}")]
    [InlineData("Content-Length: two\r\n\r\n{}")]
    [InlineData("Content-Length: 0\r\n\r\n")]
    [Theory]
    public async Task AStreamThatBreaksItsFramingIsRefused(string written) {
        using var stream = new MemoryStream(buffer: Encoding.UTF8.GetBytes(s: written));

        _ = await Assert.ThrowsAsync<InvalidDataException>(testCode: () => LspFraming.ReadAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stream: stream
        ));
    }
    [Fact]
    public async Task AServerWhoseInputBreaksItsFramingAnswersWhatCameBeforeAndEndsTheSession() {
        using var input = new MemoryStream();

        await LspFraming.WriteAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            json: LanguageServerClient.Request(
                id: 1,
                method: "initialize",
                @params: []
            ).ToJsonString(),
            stream: input
        );
        input.Write(buffer: "Content-Length: 99\r\n\r\n{"u8);
        input.Position = 0;

        using var output = new MemoryStream();

        await new PuckLanguageServer().RunAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            input: input,
            output: output
        );
        output.Position = 0;

        var response = JsonNode.Parse(json: (await LspFraming.ReadAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stream: output
        ))!);

        Assert.Equal(
            actual: response?["id"]?.GetValue<int>(),
            expected: 1
        );
        Assert.Null(@object: await LspFraming.ReadAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            stream: output
        ));
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

        var published = await LanguageServerClient.PublishedAsync(
            text: "schema: \"puck.world.definition.v1\"\nbasis: \"avatars/moth\"\n",
            uri: uri
        );

        Assert.DoesNotContain(
            collection: published,
            filter: static diagnostic => (diagnostic.Code == "PUCK035")
        );
    }
    [Fact]
    public async Task TestLspHoverAndFormatting() {
        const string Uri = "file:///test.puck";
        var transcript = await LanguageServerClient.SessionAsync(
            requests: [
                LanguageServerClient.PositionRequest(
                    character: 1,
                    id: 2,
                    line: 1,
                    method: "textDocument/hover",
                    uri: Uri
                ),
                LanguageServerClient.Request(
                    id: 3,
                    method: "textDocument/formatting",
                    @params: new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri } }
                ),
            ],
            text: "puck: 1\nhost: {\nauthority: \"test\"\n}\n",
            uri: Uri
        );
        var hoverContents = transcript.Result(id: 2)?["contents"]?["value"]?.ToString();

        Assert.NotNull(@object: hoverContents);
        Assert.Contains(
            actualString: hoverContents,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "host"
        );

        var edits = Assert.IsType<JsonArray>(@object: transcript.Result(id: 3));

        Assert.NotEmpty(collection: edits);
        Assert.Contains(
            actualString: edits[0]?["newText"]?.ToString(),
            expectedSubstring: "  authority: \"test\""
        );
    }
    [Fact]
    public async Task TestLspInitializeAndShutdown() {
        var transcript = await LanguageServerClient.ExchangeAsync(messages: [
            LanguageServerClient.Request(
                id: 1,
                method: "initialize",
                @params: []
            ),
            LanguageServerClient.Request(
                id: 2,
                method: "shutdown"
            ),
            LanguageServerClient.Notification(method: "exit"),
        ]);

        // The server answers in the order it was asked, and says nothing else.
        Assert.Equal(
            actual: transcript.Messages.Select(selector: static message => message["id"]?.GetValue<int>()),
            expected: [1, 2]
        );

        var capabilities = transcript.Result(id: 1)?["capabilities"];

        Assert.NotNull(@object: capabilities);
        Assert.True(condition: capabilities["hoverProvider"]?.GetValue<bool>());
        Assert.True(condition: capabilities["documentFormattingProvider"]?.GetValue<bool>());
    }
    [Fact]
    public async Task LspDiagnosticsReportsPuck079ForUnlockedEmbed() {
        var tempDir = Path.Combine(path1: Path.GetTempPath(), path2: ("puck_lsp_test_" + Guid.NewGuid().ToString(format: "N")));

        Directory.CreateDirectory(path: tempDir);
        try {
            var tempFile = Path.Combine(path1: tempDir, path2: "unlocked.world.puck");
            var sourceText = """
                schema: "puck.world.definition.v1"

                state {
                  spaces [
                    {
                      name: "lore"
                      dimensions: 8
                      model: "puck-fixture-v1"
                      revision: "1"
                    }
                  ]
                }

                sql {
                    CREATE TABLE lore_table (
                        id TEXT PRIMARY KEY,
                        v  VECTOR(lore)
                    );
                    INSERT INTO lore_table (id, v) VALUES ('k1', embed('unlocked text'));
                }
                """;

            await File.WriteAllTextAsync(tempFile, sourceText, TestContext.Current.CancellationToken);

            var published = await LanguageServerClient.PublishedAsync(
                text: sourceText,
                uri: new Uri(uriString: tempFile).AbsoluteUri
            );

            Assert.Contains(
                collection: published,
                filter: static diagnostic => (diagnostic.Code == "PUCK079")
            );
        } finally {
            if (Directory.Exists(path: tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
