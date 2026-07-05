using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The per-machine startup configuration: which model to emulate, the cartridge ROM image to run, an optional boot ROM
/// to execute from reset (a <see langword="null"/> image starts the machine at the model's seeded post-boot handoff
/// state instead), and the sub-cycle tick resolution its timeline runs at. It is supplied to a machine's own DI
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
    /// <param name="bootRom">The boot ROM image to execute from reset (256 bytes for a monochrome model, 0x900 bytes
    /// for Color), or <see langword="null"/> to start at the seeded post-boot handoff state.</param>
    /// <param name="tickResolution">The sub-cycle resolution of the machine's timeline, or <see langword="null"/> for
    /// <see cref="TickResolution.Default"/> (quarter ticks).</param>
    /// <exception cref="ArgumentException"><paramref name="bootRom"/> is too short to back the model's overlay.</exception>
    public MachineConfiguration(ConsoleModel model, byte[]? cartridgeRom = null, byte[]? bootRom = null, TickResolution? tickResolution = null) {
        // The overlay indexes the image directly (a monochrome model over 0x000-0x0FF, Color additionally over
        // 0x200-0x8FF), so reject an image too short to back that range rather than fault on the first fetch. The
        // AGB boot ROM shares the Color layout.
        var requiredBootRomLength = (model.SupportsColor() ? CgbBootRomLength : DmgBootRomLength);

        if ((bootRom is not null) && (bootRom.Length < requiredBootRomLength)) {
            throw new ArgumentException(
                message: $"A {model} boot ROM must be at least 0x{requiredBootRomLength:X} bytes; got 0x{bootRom.Length:X}.",
                paramName: nameof(bootRom)
            );
        }

        BootRom = bootRom;
        CartridgeRom = cartridgeRom;
        Model = model;
        TickResolution = (tickResolution ?? TickResolution.Default);
    }

    /// <summary>Gets the boot ROM image, or <see langword="null"/> to start at the seeded post-boot handoff state.</summary>
    public byte[]? BootRom { get; }
    /// <summary>Gets the cartridge ROM image, or <see langword="null"/> when no cartridge is loaded.</summary>
    public byte[]? CartridgeRom { get; }
    /// <summary>Gets the model to emulate.</summary>
    public ConsoleModel Model { get; }
    /// <summary>Gets the sub-cycle resolution of the machine's timeline.</summary>
    public TickResolution TickResolution { get; }
}
