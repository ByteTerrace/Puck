using System.CommandLine;
using System.ComponentModel;

namespace Puck.Cli.Firmware;

internal static class AgbFirmwareCommand {
    private const int BiosLength = 16_384;
    private static readonly string[] Sources = ["entrypoint.s", "boot.c", "services.c", "codec.c", "math.c", "sound.c", "multiboot.c"];

    public static Command Create() {
        var source = new Option<string>(name: "--source") { Required = true, Description = "Directory containing the maintained C/ARM sources, puck.h, and firmware.ld." };
        var output = new Option<string>(name: "--output") { Required = true, Description = "Destination 16 KiB BIOS image." };
        var clang = new Option<string>(name: "--clang") { Required = true, Description = "Path to the native Clang executable with the ARM target." };
        var linker = new Option<string>(name: "--linker") { Required = true, Description = "Path to the native ld.lld ELF linker executable." };
        var verify = new Option<bool>(name: "--verify") { Description = "Rebuild in temporary storage and compare the existing image without writing it." };
        var command = new Command(name: "agb", description: "Build the Puck AGB BIOS from its freestanding C and ARM sources.") { source, output, clang, linker, verify };

        command.SetAction(action: (result, _) => RunAsync(
            source: result.GetRequiredValue(option: source), output: result.GetRequiredValue(option: output),
            clang: result.GetRequiredValue(option: clang), linker: result.GetRequiredValue(option: linker), verify: result.GetValue(option: verify)));
        return command;
    }

    internal static async Task<int> RunAsync(string source, string output, string clang, string linker, bool verify) {
        try {
            source = Path.GetFullPath(path: source);
            output = Path.GetFullPath(path: output);
            clang = RequiredFile(path: clang);
            linker = RequiredFile(path: linker);

            foreach (var name in Sources.Concat(second: ["puck.h", "firmware.ld"])) {
                _ = RequiredFile(path: Path.Combine(path1: source, path2: name));
            }

            var bytes = await BuildAsync(source: source, clang: clang, linker: linker);
            return FirmwareArtifact.WriteOrVerify(path: output, bytes: bytes, verify: verify, machine: "agb") ? 0 : 1;
        } catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception) {
            Console.Error.WriteLine(value: $"firmware agb: {exception.Message}");
            return 1;
        }
    }

    private static async Task<byte[]> BuildAsync(string source, string clang, string linker) {
        var temporary = Directory.CreateTempSubdirectory(prefix: "puck-firmware-agb-").FullName;

        try {
            var objects = new List<string>();

            foreach (var name in Sources) {
                var path = Path.Combine(path1: temporary, path2: Path.ChangeExtension(path: name, extension: ".o"));
                var arguments = new List<string> { "--target=armv4t-none-eabi", "-mcpu=arm7tdmi" };

                if (name.EndsWith(value: ".s", comparisonType: StringComparison.Ordinal)) {
                    arguments.AddRange(collection: ["-marm", "-x", "assembler"]);
                } else {
                    arguments.AddRange(collection: ["-mthumb", "-Oz", "-ffreestanding", "-fno-builtin", "-fno-unwind-tables", "-fno-asynchronous-unwind-tables", "-fomit-frame-pointer", "-Wall", "-Wextra", "-Werror", "-std=c11"]);
                }

                arguments.AddRange(collection: ["-I", source, "-c", Path.Combine(path1: source, path2: name), "-o", path]);
                _ = await CliProcess.RunCheckedAsync(root: temporary, executable: clang, arguments: arguments, capture: true);
                objects.Add(item: path);
            }

            var image = Path.Combine(path1: temporary, path2: "firmware.bin");
            _ = await CliProcess.RunCheckedAsync(root: temporary, executable: linker,
                arguments: ["-T", Path.Combine(path1: source, path2: "firmware.ld"), "--oformat=binary", .. objects, "-o", image], capture: true);
            var bytes = File.ReadAllBytes(path: image);

            if (bytes.Length != BiosLength) {
                throw new InvalidDataException(message: $"AGB BIOS must contain exactly {BiosLength} bytes; linked image contains {bytes.Length}.");
            }

            return bytes;
        } finally {
            // Delete only the fresh directory this invocation created, never the source or output directory.
            var actual = Path.GetFullPath(path: temporary);
            var parent = Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: Path.GetTempPath()));

            if (!string.Equals(a: Path.GetDirectoryName(path: actual), b: parent, comparisonType: OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !Path.GetFileName(path: actual).StartsWith(value: "puck-firmware-agb-", comparisonType: StringComparison.Ordinal)
                || (File.GetAttributes(path: actual) & FileAttributes.ReparsePoint) != 0) {
                throw new IOException(message: "Refusing to remove a firmware build directory outside its temporary parent.");
            }

            Directory.Delete(path: actual, recursive: true);
        }
    }

    private static string RequiredFile(string path) {
        path = Path.GetFullPath(path: path);

        if (!File.Exists(path: path)) {
            throw new FileNotFoundException(message: $"Required firmware input is missing: {path}.", fileName: path);
        }

        return path;
    }
}
