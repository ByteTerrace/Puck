namespace Puck.AdvancedGamingBrick.Post;

internal static partial class Diagnostics {
    private static bool TryCompareExecution(string[] args, out int exitCode) {
        exitCode = 0;
        if (Array.IndexOf(array: args, value: "--compare-execution") < 0) {
            return false;
        }

        var rom = CommandLineArguments.Value(args: args, name: "--compare-execution");
        var frames = 600;
        var hasFrames = Array.IndexOf(array: args, value: "--frames") >= 0;
        if (!File.Exists(path: rom) || (hasFrames && (!int.TryParse(
            s: CommandLineArguments.Value(args: args, name: "--frames"), result: out frames) || (frames <= 0)))) {
            Console.Error.WriteLine(value: "--compare-execution requires an existing ROM and a positive --frames count.");
            exitCode = 2;
            return true;
        }

        var result = ExecutionComparisonProbe.Run(rom: File.ReadAllBytes(path: rom!), bios: BiosImage,
            label: Path.GetFileName(path: rom!), packets: frames, frames: true);
        Console.WriteLine(value: result.Detail);
        exitCode = (result.Pass ? 0 : 1);
        return true;
    }
}
