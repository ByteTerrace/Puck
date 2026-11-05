using System.CommandLine;
using Puck.HumbleGamingBrick;

namespace Puck.Cli.Firmware;

internal static class HgbFirmwareCommand {
    private static bool IsReported(Exception exception) =>
        (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException);

    internal static int Run(string output) {
        try {
            var directory = Path.GetFullPath(path: output);
            var images = Enum.GetValues<ConsoleModel>().AsParallel().AsOrdered().Select(selector: static model => (
                Name: (model.ToString().ToLowerInvariant() + ".bin"),
                Bytes: HgbFirmware.GetImage(model: model)
            )).ToArray();
            var success = true;

            foreach (var image in images) {
                success &= GeneratedArtifact.WriteOrVerify(
                    path: Path.Combine(
                        path1: directory,
                        path2: image.Name
                    ),
                    bytes: image.Bytes,
                    verify: false,
                    verb: "firmware hgb"
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
        var command = new Command(
            description: "Build the Puck-compatible HGB boot ROM for every ConsoleModel.",
            name: "hgb"
        ) { output };

        command.SetAction(action: result => Run(
            output: result.GetRequiredValue(option: output)
        ));
        return command;
    }
}
