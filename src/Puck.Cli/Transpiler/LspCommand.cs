using System.CommandLine;
using Puck.World.Transpiler.Lsp;

namespace Puck.Cli.Transpiler;

/// <summary><c>puck lsp</c> — launches the Puck Language Server Protocol (LSP) service over stdio.</summary>
internal static class LspCommand {
    public static Command Create() {
        var command = new Command(
            description: "Start the Puck Language Server Protocol (LSP) service over standard input/output.",
            name: "lsp"
        );

        command.SetAction(action: async _ => {
            var machines = CliWorldVocabulary.EnsureInstalled();

            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();

            var server = new PuckLanguageServer(
                catalogFingerprint: CliWorldVocabulary.Fingerprint(catalog: machines),
                completeDocument: Puck.GamingBricks.Transpiler.CartridgeLanguageServices.Completions,
                diagnoseDocument: Puck.GamingBricks.Transpiler.CartridgeLanguageServices.Diagnose,
                machines: machines,
                vocabularyResolver: CliVocabularyResolver.Instance
            );

            await server.RunAsync(
                input: stdin,
                output: stdout
            ).ConfigureAwait(continueOnCapturedContext: false);
            return 0;
        });

        return command;
    }
}
