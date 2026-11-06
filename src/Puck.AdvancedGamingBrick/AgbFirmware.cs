namespace Puck.AdvancedGamingBrick;

/// <summary>The bundled Puck native ARM firmware, independent of the host's startup mode.</summary>
public static class AgbFirmware {
    private static readonly Lazy<byte[]> Image = new(() => AgbFirmwareCompiler.GetImage());

    internal static bool Matches(ReadOnlySpan<byte> image) => image.SequenceEqual(other: Image.Value);

    /// <summary>Configures a synchronous core with bundled Puck firmware or an explicit replacement image.</summary>
    /// <param name="cartridgeRom">The native cartridge image.</param>
    /// <param name="bootMode">Cold startup by default; fast startup retains all services supplied by the selected BIOS.</param>
    /// <param name="bios">An external image, or null to select bundled Puck firmware.</param>
    /// <param name="options">Explicit diagnostic overrides, or null for normal hardware behavior.</param>
    /// <returns>The configuration for <see cref="AdvancedGamingBrickCore"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cartridgeRom"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bootMode"/> is undefined.</exception>
    /// <exception cref="InvalidOperationException">LLVM is unavailable on a cache miss.</exception>
    public static AgbMachineConfiguration CreateConfiguration(byte[] cartridgeRom, MachineBootMode bootMode = MachineBootMode.Cold, byte[]? bios = null, AgbMachineOptions? options = null) =>
        new(
            bios: (bios ?? GetImage()),
            rom: cartridgeRom,
            options: options,
            bootMode: bootMode
        );
    /// <summary>Returns the 16 KiB BIOS from the counted cache, compiling its sources when needed.</summary>
    /// <returns>The firmware bytes, owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">LLVM is unavailable and no shipping cache supplies the image.</exception>
    public static byte[] GetImage() => Image.Value.ToArray();
}
