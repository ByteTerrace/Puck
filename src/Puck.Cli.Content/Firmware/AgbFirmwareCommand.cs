using System.CommandLine;
using System.ComponentModel;

namespace Puck.Cli.Firmware;

public static class AgbFirmwareCommand {
    private const string Verb = "firmware agb";

    public static Task<int> RunAsync(string? source, string output, string clang, string linker) {
        try {
            var bytes = Puck.AdvancedGamingBrick.AgbFirmwareCompiler.GetImage(
                source: ((source is null) ? null : Path.GetFullPath(source)), clang: clang, linker: linker);
            return Task.FromResult(GeneratedArtifact.Write(bytes: bytes, path: Path.GetFullPath(output), verb: Verb) ? CliExit.Success : CliExit.Failed);
        } catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or Win32Exception) {
            return Task.FromResult(CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: Path.GetFullPath(output)), why: exception.Message));
        }
    }
    public static Command Create() {
        var source = new Option<string?>(name: "--source") { Description = "Directory containing the maintained C/ARM sources, puck.h, and firmware.ld; the sources embedded in this build when omitted." };
        var output = CliOptions.Output(description: "Destination 16 KiB BIOS image.", required: true);
        var clang = new Option<string>(name: "--clang") { DefaultValueFactory = static _ => "clang", Description = "The native Clang executable with the ARM target, on PATH or an explicit path (default clang)." };
        var linker = new Option<string>(name: "--linker") { DefaultValueFactory = static _ => "ld.lld", Description = "The native ld.lld ELF linker executable, on PATH or an explicit path (default ld.lld)." };
        var command = new Command(
            description: "Build the Puck AGB BIOS from its freestanding C and ARM sources.",
            name: "agb"
        ) { source, output, clang, linker };

        command.SetAction(action: (result, _) => RunAsync(
            source: result.GetValue(option: source),
            output: result.GetRequiredValue(option: output),
            clang: result.GetRequiredValue(option: clang),
            linker: result.GetRequiredValue(option: linker)
        ));
        return command;
    }
}
