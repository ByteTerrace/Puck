using System.CommandLine;
using Puck.World.Transpiler.Lsp;

namespace Puck.Cli.Transpiler;

/// <summary><c>puck lsp</c> — launches the Puck Language Server Protocol (LSP) service over stdio.</summary>
internal static class LspCommand {
    public static Command Create() {
        var command = new Command(
            name: "lsp",
            description: "Start the Puck Language Server Protocol (LSP) service over standard input/output."
        );

        command.SetAction(async _ => {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();

            var server = new PuckLanguageServer(stdin, stdout, Puck.GamingBricks.Transpiler.CartridgeLanguageServices.Diagnose,
                Puck.GamingBricks.Transpiler.CartridgeLanguageServices.Completions);
            await server.RunAsync().ConfigureAwait(false);
            return 0;
        });

        return command;
    }
}
