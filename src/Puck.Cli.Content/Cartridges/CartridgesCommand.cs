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

    internal static int RunDeck(string output, string? source) {
        try {
            var directory = Path.GetFullPath(path: output);
            var provider = new DeckContentProvider();
            var images = DeckTestCartridges.All.Select(selector: cartridge => (cartridge.FileName, Bytes: provider.Prepare(
                content: System.Text.Encoding.UTF8.GetBytes(s: Path.GetFileNameWithoutExtension(path: cartridge.FileName)), contentPath: Path.ChangeExtension(path: cartridge.FileName, extension: ".deck")).Image)).ToList();
            // The authored hgd cartridges compile through the same provider a Deck machine binds them with.
            var folder = (source ?? (CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root) ? Path.Combine(path1: root, path2: "worlds/debug/cartridges/deck") : null));

            if ((folder is not null) && Directory.Exists(path: folder)) {
                foreach (var path in Directory.EnumerateFiles(path: folder, searchPattern: "*.hgd.puck").Order(comparer: StringComparer.Ordinal)) {
                    images.Add(item: ($"{Path.GetFileName(path: path)[..^".hgd.puck".Length]}.nes", provider.Prepare(content: File.ReadAllBytes(path: path), contentPath: path).Image));
                }
            }

            var success = true;

            foreach (var (fileName, bytes) in images) {
                success &= GeneratedArtifact.Write(
                    bytes: bytes,
                    path: Path.Combine(
                        path1: directory,
                        path2: fileName
                    ),
                    verb: "cartridges deck"
                );
            }

            return (success
                ? 0
                : 1
            );
        } catch (Exception exception) when ((exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or Puck.Abstractions.Machines.MachineContentException)) {
            Console.Error.WriteLine(value: $"cartridges deck: {exception.Message}");
            return 1;
        }
    }

    private static Command CreateDeck() {
        var output = new Option<string>(name: "--output") { Description = "Directory to hold one .nes image per Humble Gaming Deck cartridge.", Required = true };
        var source = new Option<string?>(name: "--source") { Description = "Directory of authored *.hgd.puck cartridges to compile; the checkout's worlds/debug/cartridges/deck when omitted." };
        var command = new Command(
            description: "Build the Humble Gaming Deck cartridges: the diagnostic programs DeckProgram assembles and the authored hgd cartridges the Deck compiler builds.",
            name: "deck"
        ) { output, source };

        command.SetAction(action: result => RunDeck(
            output: result.GetRequiredValue(option: output),
            source: result.GetValue(option: source)
        ));
        return command;
    }
}
