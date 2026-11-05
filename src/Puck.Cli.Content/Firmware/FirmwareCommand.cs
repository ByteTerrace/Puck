using System.CommandLine;

namespace Puck.Cli.Firmware;

public static class FirmwareCommand {
    public static Command Create() =>
        new(
            description: "Export Puck boot firmware from its maintained source.",
            name: "firmware"
        ) {
            HgbFirmwareCommand.Create(),
            AgbFirmwareCommand.Create(),
        };
}
