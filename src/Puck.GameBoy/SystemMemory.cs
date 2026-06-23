namespace Puck.GameBoy;

/// <summary>
/// The CPU-addressable RAM the bus and PPU share, sized once from the model: object attribute memory (40 sprites
/// × 4 bytes), video RAM (one 8&#160;KiB bank on the monochrome models, two on color), and work RAM (two 4&#160;KiB
/// banks on the monochrome models, eight on color). One instance per machine, so two machines never alias each
/// other's memory; the bus and PPU hold references to the same arrays.
/// </summary>
public sealed class SystemMemory {
    private const int ObjectAttributeMemorySize = 0xA0;
    private const int VideoRamBankSize = 0x2000;
    private const int WorkRamBankSize = 0x1000;

    /// <summary>Allocates the RAM for the configured model.</summary>
    /// <param name="configuration">The machine configuration whose model sets the bank counts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public SystemMemory(MachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);

        var isColor = (configuration.Model == ConsoleModel.Cgb);

        ObjectAttributeMemory = new byte[ObjectAttributeMemorySize];
        VideoRam = new byte[VideoRamBankSize * (isColor ? 2 : 1)];
        WorkRam = new byte[WorkRamBankSize * (isColor ? 8 : 2)];
    }

    /// <summary>Gets the object attribute memory (sprite table).</summary>
    public byte[] ObjectAttributeMemory { get; }
    /// <summary>Gets the video RAM (both banks contiguous on color hardware).</summary>
    public byte[] VideoRam { get; }
    /// <summary>Gets the work RAM (all banks contiguous on color hardware).</summary>
    public byte[] WorkRam { get; }
}
