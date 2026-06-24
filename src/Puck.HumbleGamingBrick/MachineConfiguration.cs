namespace Puck.HumbleGamingBrick;

/// <summary>
/// The per-machine startup configuration: which model to emulate, an optional boot ROM to run from reset (a
/// <see langword="null"/> image starts the CPU at the model's documented post-boot state instead), and a boot
/// button combination that picks an alternative compatibility palette for a monochrome cartridge on color hardware.
/// Supplied to the per-machine scope so every subsystem reads the same model and reset settings.
/// </summary>
public sealed class MachineConfiguration {
    /// <summary>Creates a configuration.</summary>
    /// <param name="model">The model to emulate.</param>
    /// <param name="bootRom">The boot ROM image, or <see langword="null"/> to start at the post-boot state.</param>
    /// <param name="bootPalette">A button combination held at reset, selecting a compatibility palette.</param>
    public MachineConfiguration(ConsoleModel model, byte[]? bootRom = null, BootPaletteSelection bootPalette = default) {
        Model = model;
        BootRom = bootRom;
        BootPalette = bootPalette;
    }

    /// <summary>Gets the model to emulate.</summary>
    public ConsoleModel Model { get; }
    /// <summary>Gets the boot ROM image, or <see langword="null"/> to start at the post-boot state.</summary>
    public byte[]? BootRom { get; }
    /// <summary>Gets the boot button combination that selects a compatibility palette.</summary>
    public BootPaletteSelection BootPalette { get; }
}
