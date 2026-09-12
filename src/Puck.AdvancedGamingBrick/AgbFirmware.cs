namespace Puck.AdvancedGamingBrick;

/// <summary>The bundled Puck native ARM firmware, independent of the host's startup mode.</summary>
public static class AgbFirmware {
    private static readonly Lazy<byte[]> Image = new(valueFactory: LoadImage);

    /// <summary>Returns a private copy of the bundled 16 KiB image. No external toolchain is required at runtime.</summary>
    /// <returns>The firmware bytes, owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">The package is missing its firmware resource.</exception>
    public static byte[] GetImage() => Image.Value.ToArray();

    /// <summary>Configures a synchronous core with bundled Puck firmware or an explicit replacement image.</summary>
    /// <param name="cartridgeRom">The native cartridge image.</param>
    /// <param name="bootMode">Cold startup by default; fast startup retains all services supplied by the selected BIOS.</param>
    /// <param name="bios">An external image, or null to select bundled Puck firmware.</param>
    /// <param name="options">Explicit diagnostic overrides, or null for normal hardware behavior.</param>
    /// <returns>The configuration for <see cref="AdvancedGamingBrickCore"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cartridgeRom"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bootMode"/> is undefined.</exception>
    /// <exception cref="InvalidOperationException">The requested bundled resource is missing.</exception>
    public static AgbMachineConfiguration CreateConfiguration(byte[] cartridgeRom, MachineBootMode bootMode = MachineBootMode.Cold, byte[]? bios = null, AgbMachineOptions? options = null) =>
        new(bios: bios ?? GetImage(), rom: cartridgeRom, options: options, bootMode: bootMode);

    internal static bool Matches(ReadOnlySpan<byte> image) => image.SequenceEqual(other: Image.Value);

    private static byte[] LoadImage() {
        const string Name = "Puck.AdvancedGamingBrick.Firmware.puck-agb.bin";
        using var stream = typeof(AgbFirmware).Assembly.GetManifestResourceStream(name: Name)
            ?? throw new InvalidOperationException(message: $"Bundled Puck firmware '{Name}' is missing.");
        var image = new byte[ReplacementBios.ImageSize];
        stream.ReadExactly(buffer: image);
        return image;
    }
}
