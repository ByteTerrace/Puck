using System.CommandLine;
using Puck.HumbleGamingDeck.Forge;

namespace Puck.Cli.Cartridges;

public static class CartridgesCommand {
    public static Command Create() =>
        new(
            description: "Export cartridges built by repository tooling.",
            name: "cartridges"
        ) {
            CreateDeck(),
        };

    internal static int RunDeck(string output) {
        try {
            var directory = Path.GetFullPath(path: output);
            var provider = new DeckContentProvider();
            var images = DeckTestCartridges.All.Select(selector: cartridge => (cartridge.FileName, Bytes: provider.Prepare(
                content: System.Text.Encoding.UTF8.GetBytes(s: Path.GetFileNameWithoutExtension(path: cartridge.FileName)), contentPath: Path.ChangeExtension(path: cartridge.FileName, extension: ".deck")).Image)).ToArray();
            var success = true;

            foreach (var (fileName, bytes) in images) {
                success &= GeneratedArtifact.WriteOrVerify(
                    bytes: bytes,
                    path: Path.Combine(
                        path1: directory,
                        path2: fileName
                    ),
                    verb: "cartridges deck",
                    verify: false
                );
            }

            return (success
                ? 0
                : 1
            );
        } catch (Exception exception) when ((exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)) {
            Console.Error.WriteLine(value: $"cartridges deck: {exception.Message}");
            return 1;
        }
    }

    private static Command CreateDeck() {
        var output = new Option<string>(name: "--output") { Description = "Directory to hold one .nes image per Humble Gaming Deck test cartridge.", Required = true };
        var command = new Command(
            description: "Build the Humble Gaming Deck test cartridges (NROM images assembled by DeckProgram).",
            name: "deck"
        ) { output };

        command.SetAction(action: result => RunDeck(
            output: result.GetRequiredValue(option: output)
        ));
        return command;
    }
}
