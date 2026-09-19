using System.Text;
using System.Text.Json.Nodes;
using Puck.Cli.Transpiler;
using Puck.GamingBricks.Forge;
using Puck.GamingBricks.Transpiler;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class CartridgeToolingTests {
    private static string Source(string target) => CartridgeDecompiler.Decompile(document: JsonNode.Parse(CartridgeDocuments.Canonicalize(document: CartridgeDocuments.Create(
        target: target,
        title: "TEST"
    )).Bytes)!.AsObject());

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void CompileAndLintDispatchCartridgeSchema(string target) {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-cartridge-tooling-" + Guid.NewGuid().ToString(format: "N"))
        );

        Directory.CreateDirectory(path: directory);
        try {
            var path = Path.Combine(
                path1: directory,
                path2: "example.puck"
            );

            File.WriteAllText(
                path,
                Source(target: target)
            );
            Assert.Equal(
                0,
                CompileCommand.Run(
                    path,
                    null,
                    false,
                    true,
                    true,
                    false
                )
            );
            Assert.True(condition: File.Exists(path: Path.ChangeExtension(
                extension: ".cartridge.json",
                path: path
            )));
            Assert.False(condition: File.Exists(path: Path.ChangeExtension(
                extension: ".world.json",
                path: path
            )));
            Assert.Equal(
                0,
                LintCommand.Execute(
                    path,
                    true
                )
            );
            File.AppendAllText(
                contents: "\nvariable \"invalid\" { initial: 99999 }\n",
                path: path
            );
            Assert.Equal(
                1,
                LintCommand.Execute(
                    path,
                    true
                )
            );
            Assert.Equal(
                1,
                CompileCommand.Run(
                    path,
                    null,
                    false,
                    true,
                    true,
                    false
                )
            );
        } finally { Directory.Delete(
            directory,
            recursive: true
        ); }
    }
    [Fact]
    public async Task UnsavedCartridgeBufferUsesForgeDiagnostics() {
        var request = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/didOpen",
            ["params"] = new JsonObject {
                ["textDocument"] = new JsonObject {
                    ["uri"] = "untitled:cartridge",
                    ["text"] = (Source(target: "cgb") + "\nvariable \"invalid\" { initial: 99999 }\n"),
                },
            },
        }.ToJsonString();
        var body = Encoding.UTF8.GetBytes(s: request);
        using var input = new MemoryStream();

        input.Write(buffer: Encoding.ASCII.GetBytes(s: $"Content-Length: {body.Length}\r\n\r\n"));
        input.Write(buffer: body);
        input.Position = 0;
        using var output = new MemoryStream();
        // The server `puck lsp` builds: a source's declared schema selects its vocabulary, and a vocabulary other
        // than the world's is diagnosed by the dispatcher.
        var server = new PuckLanguageServer(
            diagnoseDocument: CartridgeLanguageServices.Diagnose,
            input: input,
            output: output,
            vocabularyResolver: Puck.Cli.Transpiler.CliVocabularyResolver.Instance
        );

        await server.RunAsync(cancellationToken: TestContext.Current.CancellationToken);
        var messages = Encoding.UTF8.GetString(bytes: output.ToArray());

        Assert.Contains(
            actualString: messages,
            expectedSubstring: "textDocument/publishDiagnostics"
        );
        Assert.Contains(
            actualString: messages,
            expectedSubstring: "initial"
        );
        Assert.DoesNotContain(
            actualString: messages,
            expectedSubstring: "LSP parse error"
        );
    }
}
