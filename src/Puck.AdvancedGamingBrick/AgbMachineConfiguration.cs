namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The per-machine startup configuration: the BIOS image the machine boots with and the cartridge ROM image it runs.
/// Both are immutable inputs the machine never writes back, so a fork rebuilds an identical sibling from the very same
/// configuration — the same BIOS and ROM bytes shared verbatim — and the two machines differ only in the state they go
/// on to accumulate. It is retained by an <see cref="AgbMachineInstance"/> precisely so a fork can reconstruct the
/// machine's inputs without the caller re-supplying them.
/// </summary>
public sealed class AgbMachineConfiguration {
    /// <summary>Creates a configuration.</summary>
    /// <param name="bios">The 16&#160;KiB BIOS image (a zeroed stub is valid; only the callers that need a real BIOS
    /// check for one).</param>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public AgbMachineConfiguration(ReadOnlyMemory<byte> bios, byte[] rom) {
        ArgumentNullException.ThrowIfNull(argument: rom);

        Bios = bios;
        Rom = rom;
    }

    /// <summary>Gets the BIOS image the machine boots with.</summary>
    public ReadOnlyMemory<byte> Bios { get; }
    /// <summary>Gets the cartridge ROM image the machine runs.</summary>
    public byte[] Rom { get; }
}
