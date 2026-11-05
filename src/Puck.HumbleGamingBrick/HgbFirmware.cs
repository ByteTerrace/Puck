namespace Puck.HumbleGamingBrick;

/// <summary>The bundled, original Puck startup firmware for each SM83 hardware revision.</summary>
public static class HgbFirmware {
    private static readonly Lazy<Puck.Assets.DerivedBytes> Cache = new(valueFactory: static () => new Puck.Assets.DerivedBytes(
        name: "hgb-firmware", roots: [("Puck.HumbleGamingBrick", "Puck.HumbleGamingBrick.BootRomBuilder", "Build")]));
    /// <summary>Configures a cartridge with bundled Puck firmware or an explicit replacement image.</summary>
    /// <param name="model">The hardware revision to emulate.</param>
    /// <param name="cartridgeRom">The native cartridge bytes.</param>
    /// <param name="bootMode">Cold startup by default; fast startup skips presentation but retains firmware identity.</param>
    /// <param name="bootRom">An external image, or null to select the bundled image for this revision.</param>
    /// <returns>The configuration, ready for a synchronous core or a machine factory.</returns>
    /// <exception cref="ArgumentException"><paramref name="bootRom"/> is too short for the selected model.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bootMode"/> is undefined, or the bundled
    /// image was requested for an undefined <paramref name="model"/>.</exception>
    public static MachineConfiguration CreateConfiguration(ConsoleModel model, byte[] cartridgeRom, MachineBootMode bootMode = MachineBootMode.Cold, byte[]? bootRom = null) =>
        new(
            model: model,
            cartridgeRom: cartridgeRom,
            bootRom: (bootRom ?? GetImage(model: model)),
            bootMode: bootMode
        );
    /// <summary>Derives the revision's startup image in process on a cache miss, or reads a private cached copy.</summary>
    /// <param name="model">The hardware revision whose handoff the image implements.</param>
    /// <returns>A 256-byte monochrome image or a 2304-byte Color-compatible image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="model"/> is not a defined hardware revision.</exception>
    /// <param name="cacheDirectory">An explicit cache directory, or the per-user derived cache.</param>
    /// <exception cref="InvalidOperationException">The boot program's timing cannot be calibrated.</exception>
    public static byte[] GetImage(ConsoleModel model, string? cacheDirectory = null) {
        if (!Enum.IsDefined(value: model)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(model));
        }

        return Cache.Value.Get(inputs: model.ToString(), derive: () => BootRomBuilder.Build(model: model), directory: cacheDirectory);
    }
}
