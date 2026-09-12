using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The per-machine startup configuration: which model to emulate, the cartridge ROM image to run, an optional boot ROM
/// and an independent startup mode, plus the sub-cycle tick resolution its timeline runs at. A fast start seeds the
/// model's post-boot handoff while retaining the selected firmware in snapshot identity. It is supplied to a machine's own DI
/// container, so every subsystem in that machine reads the same settings and two machines built from two configurations
/// never share any state. A fork copies the source's configuration verbatim — including the same immutable ROM images —
/// so the divergent machine is identical in everything but the state it goes on to accumulate.
/// </summary>
public sealed class MachineConfiguration {
    private const int CgbBootRomLength = 0x0900;
    private const int DmgBootRomLength = 0x0100;

    /// <summary>Creates a configuration.</summary>
    /// <param name="model">The model to emulate.</param>
    /// <param name="cartridgeRom">The cartridge ROM image, or <see langword="null"/> when no cartridge is loaded (for
    /// example a bare timing harness).</param>
    /// <param name="bootRom">The selected boot ROM (at least 256 bytes for a monochrome model, 0x900 bytes for Color),
    /// retained even in fast mode. Null permits only the seeded post-boot handoff. The image is borrowed and callers
    /// must not mutate it while a machine or fork uses this configuration.</param>
    /// <param name="tickResolution">The sub-cycle resolution of the machine's timeline, or <see langword="null"/> for
    /// <see cref="TickResolution.Default"/> (quarter ticks).</param>
    /// <param name="bootMode">Cold startup executes the image; fast startup seeds the handoff. Null selects cold when
    /// an image is supplied and fast otherwise, preserving the low-level diagnostic constructor's explicit inputs.</param>
    /// <exception cref="ArgumentException"><paramref name="bootRom"/> is too short to back the model's overlay,
    /// or cold startup was selected without an image.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bootMode"/> is not a defined mode.</exception>
    public MachineConfiguration(ConsoleModel model, byte[]? cartridgeRom = null, byte[]? bootRom = null, TickResolution? tickResolution = null, MachineBootMode? bootMode = null) {
        // The overlay indexes the image directly (a monochrome model over 0x000-0x0FF, Color additionally over
        // 0x200-0x8FF), so reject an image too short to back that range rather than fault on the first fetch. The
        // AGB boot ROM shares the Color layout.
        var requiredBootRomLength = (model.SupportsColor()
            ? CgbBootRomLength
            : DmgBootRomLength);

        if (
            (bootRom is not null) &&
            (bootRom.Length < requiredBootRomLength)
        ) {
            throw new ArgumentException(
                message: $"A {model} boot ROM must be at least 0x{requiredBootRomLength:X} bytes; got 0x{bootRom.Length:X}.",
                paramName: nameof(bootRom)
            );
        }

        BootMode = bootMode ?? (bootRom is null ? MachineBootMode.Fast : MachineBootMode.Cold);
        if (BootMode is not (MachineBootMode.Cold or MachineBootMode.Fast)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(bootMode));
        }
        if ((BootMode == MachineBootMode.Cold) && (bootRom is null)) {
            throw new ArgumentException(message: "Cold startup requires a boot ROM image.", paramName: nameof(bootRom));
        }

        BootRom = bootRom;
        CartridgeRom = cartridgeRom;
        Model = model;
        TickResolution = (tickResolution ?? TickResolution.Default);
    }

    /// <summary>Gets the selected boot ROM image, retained even when startup is skipped.</summary>
    public byte[]? BootRom { get; }
    /// <summary>Gets whether startup executes firmware or begins at the seeded cartridge handoff.</summary>
    public MachineBootMode BootMode { get; }
    internal bool ExecutesBootRom => BootMode == MachineBootMode.Cold;
    /// <summary>Gets the cartridge ROM image, or <see langword="null"/> when no cartridge is loaded.</summary>
    public byte[]? CartridgeRom { get; }
    /// <summary>Gets the model to emulate.</summary>
    public ConsoleModel Model { get; }
    /// <summary>Gets the sub-cycle resolution of the machine's timeline.</summary>
    public TickResolution TickResolution { get; }
}
