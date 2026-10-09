using System.CommandLine;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;

namespace Puck.Cli.Firmware;

internal static class HgbFirmwareCommand {
    private static bool IsReported(Exception exception) =>
        (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException);

    internal static int Run(string output, bool verify) {
        try {
            var directory = Path.GetFullPath(path: output);
            // Finish generation before replacing any checked-in file. A failed calibration cannot leave half a set.
            // Each revision's image is calibrated by booting its own emulated machines and shares no state with
            // another's, so the revisions build concurrently and land in model order.
            var images = Enum.GetValues<ConsoleModel>().AsParallel().AsOrdered().Select(selector: static model => (
                Name: (model.ToString().ToLowerInvariant() + ".bin"),
                Bytes: BootRomBuilder.Build(
                mark: BootRomMark.Compatible,
                model: model
            )
            )).ToArray();
            var success = true;

            foreach (var image in images) {
                success &= FirmwareArtifact.WriteOrVerify(
                    path: Path.Combine(
                        path1: directory,
                        path2: image.Name
                    ),
                    bytes: image.Bytes,
                    verify: verify,
                    machine: "hgb"
                );
            }

            return (success
                ? 0
                : 1
            );
        } catch (AggregateException exception) when (exception.Flatten().InnerExceptions.All(predicate: IsReported)) {
            // A revision that fails to calibrate surfaces from the concurrent build wrapped with any others that failed.
            foreach (var inner in exception.Flatten().InnerExceptions) {
                Console.Error.WriteLine(value: $"firmware hgb: {inner.Message}");
            }
            return 1;
        } catch (Exception exception) when (IsReported(exception: exception)) {
            Console.Error.WriteLine(value: $"firmware hgb: {exception.Message}");
            return 1;
        }
    }

    public static Command Create() {
        var output = new Option<string>(name: "--output") { Description = "Directory containing one lowercase model-name .bin image per hardware revision.", Required = true };
        var verify = new Option<bool>(name: "--verify") { Description = "Compare every existing image byte-for-byte without writing files." };
        var command = new Command(
            description: "Build the Puck-compatible HGB boot ROM for every ConsoleModel.",
            name: "hgb"
        ) { output, verify };

        command.SetAction(action: result => Run(
            output: result.GetRequiredValue(option: output),
            verify: result.GetValue(option: verify)
        ));
        return command;
    }
}
