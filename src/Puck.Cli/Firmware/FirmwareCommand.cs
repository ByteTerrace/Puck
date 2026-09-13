using System.CommandLine;

namespace Puck.Cli.Firmware;

internal static class FirmwareCommand {
    public static Command Create() =>
        new(
            description: "Rebuild or verify the bundled Puck boot firmware from its maintained source.",
            name: "firmware"
        ) {
            HgbFirmwareCommand.Create(),
            AgbFirmwareCommand.Create(),
        };
}
