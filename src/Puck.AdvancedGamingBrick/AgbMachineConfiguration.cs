namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The per-machine startup configuration: the BIOS image, cartridge ROM, startup mode and explicit diagnostic options.
/// The images are borrowed inputs the caller must not change while any machine or fork uses them. The machine never
/// writes back to them, so a fork rebuilds an identical sibling from the very same
/// configuration — the same BIOS and ROM bytes shared verbatim — and the two machines differ only in the state they go
/// on to accumulate. It is retained by an <see cref="AgbMachineInstance"/> precisely so a fork can reconstruct the
/// machine's inputs without the caller re-supplying them.
/// </summary>
public sealed class AgbMachineConfiguration {
    /// <summary>Creates a configuration.</summary>
    /// <param name="bios">The 16&#160;KiB BIOS image. A zeroed stub supports only BIOS-independent direct-boot
    /// cartridges; it provides no SWI services or IRQ dispatch.</param>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <param name="options">Per-machine diagnostic overrides, or null for normal hardware behavior.</param>
    /// <param name="bootMode">Cold startup executes the BIOS from reset. Fast startup seeds the cartridge handoff
    /// while keeping the same BIOS available for software interrupts and IRQ dispatch.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bootMode"/> is not a defined mode.</exception>
    public AgbMachineConfiguration(ReadOnlyMemory<byte> bios, byte[] rom, AgbMachineOptions? options = null, MachineBootMode bootMode = MachineBootMode.Fast) {
        ArgumentNullException.ThrowIfNull(argument: rom);
        if (bootMode is not (MachineBootMode.Cold or MachineBootMode.Fast)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(bootMode));
        }

        Bios = bios;
        BootMode = bootMode;
        Rom = rom;
        Options = options ?? new AgbMachineOptions();
    }

    /// <summary>Gets the BIOS image the machine boots with.</summary>
    public ReadOnlyMemory<byte> Bios { get; }
    /// <summary>Gets whether the host executes firmware from reset or seeds the cartridge handoff.</summary>
    public MachineBootMode BootMode { get; }
    /// <summary>Gets the cartridge ROM image the machine runs.</summary>
    public byte[] Rom { get; }
    /// <summary>Gets the explicit per-machine options retained by forks.</summary>
    public AgbMachineOptions Options { get; }
}
