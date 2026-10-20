using System.CommandLine;
using Puck.HumbleGamingDeck.Forge;

namespace Puck.Cli.Cartridges;

public static class CartridgesCommand {
    public static Command Create() =>
        new(
            description: "Rebuild or verify the shipped cartridges that are built by repository tooling rather than compiled from a .puck source.",
            name: "cartridges"
        ) {
            CreateDeck(),
        };

    internal static int RunDeck(string output, bool check) {
        try {
            var directory = Path.GetFullPath(path: output);
            var images = DeckTestCartridges.All.Select(selector: static cartridge => (cartridge.FileName, Bytes: cartridge.Build())).ToArray();
            var success = true;

            foreach (var (fileName, bytes) in images) {
                success &= GeneratedArtifact.WriteOrVerify(
                    bytes: bytes,
                    path: Path.Combine(
                        path1: directory,
                        path2: fileName
                    ),
                    verb: "cartridges deck",
                    verify: check
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
        var check = new Option<bool>(name: "--check") { Description = "Compare every existing image byte-for-byte without writing files; exit 1 on drift." };
        var command = new Command(
            description: "Build the Humble Gaming Deck test cartridges (NROM images assembled by DeckProgram).",
            name: "deck"
        ) { check, output };

        command.SetAction(action: result => RunDeck(
            output: result.GetRequiredValue(option: output),
            check: result.GetValue(option: check)
        ));
        return command;
    }
}
