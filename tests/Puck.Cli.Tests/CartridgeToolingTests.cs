using System.Text;
using System.Text.Json.Nodes;
using Puck.Cli.Transpiler;
using Puck.GamingBricks.Forge;
using Puck.GamingBricks.Transpiler;
using Puck.World.Transpiler.Lsp;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class CartridgeToolingTests {
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void CompileAndLintDispatchCartridgeSchema(string target) {
        var directory = Path.Combine(Path.GetTempPath(), "puck-cartridge-tooling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var path = Path.Combine(directory, "example.puck");
            File.WriteAllText(path, Source(target));
            Assert.Equal(0, CompileCommand.Run(path, null, false, true, true, false));
            Assert.True(File.Exists(Path.ChangeExtension(path, ".cartridge.json")));
            Assert.False(File.Exists(Path.ChangeExtension(path, ".world.json")));
            Assert.Equal(0, LintCommand.Execute(path, true));
            File.AppendAllText(path, "\nvariable \"invalid\" { initial: 99999 }\n");
            Assert.Equal(1, LintCommand.Execute(path, true));
            Assert.Equal(1, CompileCommand.Run(path, null, false, true, true, false));
        } finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task UnsavedCartridgeBufferUsesForgeDiagnostics() {
        var request = new JsonObject {
            ["jsonrpc"] = "2.0", ["method"] = "textDocument/didOpen",
            ["params"] = new JsonObject {
                ["textDocument"] = new JsonObject {
                    ["uri"] = "untitled:cartridge", ["text"] = Source("cgb") + "\nvariable \"invalid\" { initial: 99999 }\n",
                },
            },
        }.ToJsonString();
        var body = Encoding.UTF8.GetBytes(request);
        using var input = new MemoryStream();
        input.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        input.Write(body);
        input.Position = 0;
        using var output = new MemoryStream();
        var server = new PuckLanguageServer(input, output, CartridgeLanguageServices.Diagnose);
        await server.RunAsync(TestContext.Current.CancellationToken);
        var messages = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("textDocument/publishDiagnostics", messages);
        Assert.Contains("initial", messages);
        Assert.DoesNotContain("LSP parse error", messages);
    }

    private static string Source(string target) => CartridgeDecompiler.Decompile(
        JsonNode.Parse(CartridgeDocuments.Canonicalize(CartridgeDocuments.Create(target, "TEST")).Bytes)!.AsObject());
}
