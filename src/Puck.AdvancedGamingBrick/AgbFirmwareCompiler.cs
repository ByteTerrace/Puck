using Puck.Assets;

namespace Puck.AdvancedGamingBrick;

/// <summary>The one native build of Puck's authored ARM firmware, used by runtime misses and exports.</summary>
public static class AgbFirmwareCompiler {
    private static readonly string[] Sources = ["entrypoint.s", "boot.c", "services.c", "codec.c", "math.c", "sound.c", "multiboot.c"];
    private static readonly Lazy<DerivedBytes> Cache = new(() => new("agb-firmware",
        [("Puck.AdvancedGamingBrick", "Puck.AdvancedGamingBrick.AgbFirmwareCompiler", "Build")]));

    /// <summary>Returns the cached BIOS or compiles it, identifying both LLVM tools and all source inputs.</summary>
    /// <param name="source">Live firmware directory, or null for the sources embedded in this build.</param>
    /// <param name="clang">Clang executable on PATH, or an explicit path.</param>
    /// <param name="linker">ELF LLD executable on PATH, or an explicit path.</param>
    /// <param name="cacheDirectory">Explicit local derived cache, or the per-user cache.</param>
    /// <param name="packageDirectory">Shipping cache to read or prewarm.</param>
    /// <param name="prewarm">Populate the shipping cache using the installed toolchain.</param>
    /// <returns>The caller's BIOS bytes.</returns>
    public static byte[] GetImage(string? source = null, string clang = "clang", string linker = "ld.lld",
        string? cacheDirectory = null, string? packageDirectory = null, bool prewarm = false) {
        var files = ToolchainBuild.Sources(typeof(AgbFirmware).Assembly, "Puck.AgbSources/", source);
        return Cache.Value.GetToolchain(ToolchainBuild.Identity(files),
            () => ToolchainBuild.Identify(clang, new Version(19, 1, 6)) + "\n" + ToolchainBuild.Identify(linker, new Version(19, 1, 6)),
            () => ToolchainBuild.Derive(files, directory => Build(directory, clang, linker)),
            cacheDirectory, packageDirectory, prewarm);
    }

    private static byte[] Build(string directory, string clang, string linker) {
        var objects = new List<string>();
        foreach (var name in Sources) {
            var output = Path.ChangeExtension(name, ".o");
            string[] options = name.EndsWith(".s", StringComparison.Ordinal)
                ? ["-marm", "-x", "assembler"]
                : ["-mthumb", "-Oz", "-ffreestanding", "-fno-builtin", "-fno-unwind-tables", "-fno-asynchronous-unwind-tables", "-fomit-frame-pointer", "-Wall", "-Wextra", "-Werror", "-std=c11"];
            ToolchainBuild.Run(clang, ["--target=armv4t-none-eabi", "-mcpu=arm7tdmi", "-nostdinc", $"-ffile-prefix-map={directory}=.", .. options, "-I", ".", "-c", name, "-o", output], directory);
            objects.Add(output);
        }
        ToolchainBuild.Run(linker, ["-T", "firmware.ld", "--oformat=binary", .. objects, "-o", "firmware.bin"], directory);
        var bytes = File.ReadAllBytes(Path.Combine(directory, "firmware.bin"));
        if (bytes.Length != ReplacementBios.ImageSize) { throw new InvalidDataException($"AGB BIOS must contain exactly {ReplacementBios.ImageSize} bytes; got {bytes.Length}."); }
        return bytes;
    }
}
