using System.CommandLine;
using System.ComponentModel;

namespace Puck.Cli.Firmware;

public static class AgbFirmwareCommand {
    public static Task<int> RunAsync(string source, string output, string clang, string linker) {
        try {
            var bytes = Puck.AdvancedGamingBrick.AgbFirmwareCompiler.GetImage(
                source: Path.GetFullPath(source), clang: clang, linker: linker);
            return Task.FromResult(GeneratedArtifact.Write(bytes: bytes, path: Path.GetFullPath(output), verb: "firmware agb") ? 0 : 1);
        } catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception) {
            Console.Error.WriteLine($"firmware agb: {exception.Message}");
            return Task.FromResult(1);
        }
    }
    public static Command Create() {
        var source = new Option<string>(name: "--source") { Description = "Directory containing the maintained C/ARM sources, puck.h, and firmware.ld.", Required = true };
        var output = new Option<string>(name: "--output") { Description = "Destination 16 KiB BIOS image.", Required = true };
        var clang = new Option<string>(name: "--clang") { Description = "Path to the native Clang executable with the ARM target.", Required = true };
        var linker = new Option<string>(name: "--linker") { Description = "Path to the native ld.lld ELF linker executable.", Required = true };
        var command = new Command(
            description: "Build the Puck AGB BIOS from its freestanding C and ARM sources.",
            name: "agb"
        ) { source, output, clang, linker };

        command.SetAction(action: (result, _) => RunAsync(
            source: result.GetRequiredValue(option: source),
            output: result.GetRequiredValue(option: output),
            clang: result.GetRequiredValue(option: clang),
            linker: result.GetRequiredValue(option: linker)
        ));
        return command;
    }
}
