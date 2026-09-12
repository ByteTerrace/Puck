using System.CommandLine;

namespace Puck.Cli.Firmware;

internal static class FirmwareCommand {
    public static Command Create() =>
        new(name: "firmware", description: "Rebuild or verify the bundled Puck boot firmware from its maintained source.") {
            HgbFirmwareCommand.Create(),
            AgbFirmwareCommand.Create(),
        };
}
