namespace Puck.HumbleGamingBrick;

/// <summary>The bundled, original Puck startup firmware for each SM83 hardware revision.</summary>
public static class HgbFirmware {
    /// <summary>Loads a private copy of the bundled image. Runtime consumers do not need the Forge package.</summary>
    /// <param name="model">The hardware revision whose handoff the image implements.</param>
    /// <returns>A 256-byte monochrome image or a 2304-byte Color-compatible image.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="model"/> is not a defined hardware revision.</exception>
    /// <exception cref="InvalidOperationException">The package is missing its firmware resource.</exception>
    public static byte[] GetImage(ConsoleModel model) {
        if (!Enum.IsDefined(value: model)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(model));
        }

        var name = $"Puck.HumbleGamingBrick.Firmware.{model.ToString().ToLowerInvariant()}.bin";
        using var stream = typeof(HgbFirmware).Assembly.GetManifestResourceStream(name: name)
            ?? throw new InvalidOperationException(message: $"Bundled Puck firmware '{name}' is missing.");
        var image = new byte[model.SupportsColor() ? 0x900 : 0x100];
        stream.ReadExactly(buffer: image);
        return image;
    }

    /// <summary>Configures a cartridge with bundled Puck firmware or an explicit replacement image.</summary>
    /// <param name="model">The hardware revision to emulate.</param>
    /// <param name="cartridgeRom">The native cartridge bytes.</param>
    /// <param name="bootMode">Cold startup by default; fast startup skips presentation but retains firmware identity.</param>
    /// <param name="bootRom">An external image, or null to select the bundled image for this revision.</param>
    /// <returns>The configuration, ready for a synchronous core or a machine factory.</returns>
    /// <exception cref="ArgumentException"><paramref name="bootRom"/> is too short for the selected model.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bootMode"/> is undefined, or the bundled
    /// image was requested for an undefined <paramref name="model"/>.</exception>
    /// <exception cref="InvalidOperationException">The requested bundled resource is missing.</exception>
    public static MachineConfiguration CreateConfiguration(ConsoleModel model, byte[] cartridgeRom, MachineBootMode bootMode = MachineBootMode.Cold, byte[]? bootRom = null) =>
        new(model: model, cartridgeRom: cartridgeRom, bootRom: bootRom ?? GetImage(model: model), bootMode: bootMode);
}
